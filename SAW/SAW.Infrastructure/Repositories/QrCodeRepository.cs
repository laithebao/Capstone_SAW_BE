using System.Data;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.QrCodes;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class QrCodeRepository(AppDbContext db) : IQrCodeRepository
{
    public async Task<BatchQrSnapshot?> GetAsync(long batchId, CancellationToken ct)
    {
        // One SQL statement: batch, latest QC and QR are read together.
        var data = await db.ProductBatches.AsNoTracking().Where(b => b.ProductBatchId == batchId)
            .Select(b => new
            {
                Batch = b,
                Latest = b.QcInspections.OrderByDescending(q => q.StartedAt)
                    .ThenByDescending(q => q.QcInspectionId).FirstOrDefault(),
                Codes = b.QrCodes.Where(q => q.PackageCode == null).OrderBy(q => q.QrCodeId).ToList()
            }).SingleOrDefaultAsync(ct);
        return data is null ? null : new(data.Batch, data.Latest, data.Codes);
    }

    public async Task<IReadOnlyList<long>> GetCandidatesAsync(long afterId, int take, CancellationToken ct) =>
        await db.ProductBatches.AsNoTracking()
            .Where(b => b.ProductBatchId > afterId && QrCodeEligibility.CreationStatuses.Contains(b.BatchStatus)
                && !b.QrCodes.Any(q => q.PackageCode == null && (!q.IsActive || (q.QrImageUrl != null && q.QrImageUrl != ""))))
            .Where(b => b.QcInspections.OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId)
                .Take(1).Any(q => q.InspectionStatus == "COMPLETED" && q.QcResult == "PASS"
                    && q.CompletedAt != null && q.CompletedAt >= q.StartedAt
                    && (q.QualityGrade == "A" || q.QualityGrade == "B" || q.QualityGrade == "C" || q.QualityGrade == "D")
                    && q.QualityGrade == b.QualityGrade))
            .OrderBy(b => b.ProductBatchId).Select(b => b.ProductBatchId).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);

    public Task<bool> IsImageReferencedAsync(string imageUrl, CancellationToken ct) =>
        db.QrCodes.AsNoTracking().AnyAsync(q => q.QrImageUrl == imageUrl, ct);

    public async Task<QrCode?> SaveGeneratedAsync(long batchId, string token, string url, string imageUrl, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // This repository is used in a dedicated worker scope. Reset failed attempt
            // entities/audits before retry, and reconstruct from committed state.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var batch = await db.ProductBatches
                .FromSqlInterpolated($"SELECT * FROM PRODUCT_BATCH WITH (UPDLOCK, HOLDLOCK) WHERE ProductBatchID = {batchId}")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (batch is null) return null;
            // Serializable range locks prevent a new QC inspection or changes to latest QC
            // between this check and QR commit. No Cloudinary request runs in this transaction.
            var latest = await db.QcInspections.AsNoTracking().Where(q => q.ProductBatchId == batchId)
                .OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId).FirstOrDefaultAsync(ct);
            var codes = await db.QrCodes.Where(q => q.ProductBatchId == batchId && q.PackageCode == null)
                .OrderBy(q => q.QrCodeId).ToListAsync(ct);
            if (codes.Any(q => !q.IsActive)) return null;
            var code = codes.FirstOrDefault();
            if (code is not null && QrCodeEligibility.HasImage(code)) return code;
            if (!QrCodeEligibility.CanCreate(batch, latest)) return null;
            if (code is null)
            {
                code = new QrCode { ProductBatchId = batchId, PackageCode = null, PublicToken = token,
                    TraceabilityUrl = url, QrImageUrl = imageUrl, IsActive = true, GeneratedAt = DateTime.UtcNow };
                db.QrCodes.Add(code);
            }
            else
            {
                // Do not attach an image encoded for a stale/different token during repair.
                if (code.PublicToken != token || code.TraceabilityUrl != url) return null;
                code.QrImageUrl = imageUrl;
            }
            // AppDbContext writes QR + audit atomically. Background scope has no account
            // claims => system actor NULL. ProductBatch remains entirely unmodified.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return code;
        });
    }
}
