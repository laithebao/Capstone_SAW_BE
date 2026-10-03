using Microsoft.EntityFrameworkCore;
using System.Data;
using SAW.Application.Exceptions;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class QcInspectionRepository(AppDbContext db) : IQcInspectionRepository
{
    // ── Lookup ────────────────────────────────────────────────────────────────

    public Task<ProductBatch?> GetBatchAsync(long batchId, CancellationToken ct)
        => db.ProductBatches
            .Include(b => b.CropType)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.ProductBatchId == batchId, ct);

    public Task<InspectionStandardVersion?> GetVersionWithCriteriaAsync(
        long versionId, CancellationToken ct)
        => db.InspectionStandardVersions
            .Include(v => v.InspectionStandardSet)
            .Include(v => v.Criteria)
                .ThenInclude(c => c.GradeRules)
            .FirstOrDefaultAsync(v => v.InspectionStandardVersionId == versionId, ct);

    public Task<bool> HasActiveInspectionAsync(long batchId, CancellationToken ct)
        => db.QcInspections.AnyAsync(
            q => q.ProductBatchId == batchId
              && q.InspectionStatus != "COMPLETED",
            ct);

    public Task<bool> InspectionCodeExistsAsync(string code, CancellationToken ct)
        => db.QcInspections.AnyAsync(q => q.InspectionCode == code, ct);

    public Task<QcInspection?> GetInspectionAsync(long id, CancellationToken ct)
        => db.QcInspections
            .Include(q => q.ProductBatch)
            .Include(q => q.QcAccount)
            .Include(q => q.SensoryResult)
            .Include(q => q.LabResult)
            .Include(q => q.ResultDetails)
                .ThenInclude(d => d.InspectionCriterion)
            .FirstOrDefaultAsync(q => q.QcInspectionId == id, ct);

    public Task<QcInspection?> GetInspectionDetailAsync(long id, CancellationToken ct)
        => db.QcInspections
            .Include(q => q.ProductBatch)
                .ThenInclude(b => b.CropType)
            .Include(q => q.QcAccount)
            .Include(q => q.InspectionStandardVersion)
                .ThenInclude(v => v.InspectionStandardSet)
            .Include(q => q.InspectionStandardVersion)
                .ThenInclude(v => v.Criteria).ThenInclude(c => c.GradeRules)
            .Include(q => q.SensoryResult)
            .Include(q => q.LabResult)
            .Include(q => q.ResultDetails)
                .ThenInclude(d => d.InspectionCriterion)
                    .ThenInclude(c => c.GradeRules)
            .Include(q => q.QualityImages)
                .ThenInclude(img => img.UploadedByAccount)
            .Include(q => q.EnvironmentLogs)
                .ThenInclude(e => e.WarehouseLocation)
            .FirstOrDefaultAsync(q => q.QcInspectionId == id, ct);

    public Task<int> CountImagesAsync(long inspectionId, CancellationToken ct)
        => db.QualityImages.CountAsync(img => img.QcInspectionId == inspectionId, ct);

    public Task<QualityImage?> GetImageAsync(long imageId, CancellationToken ct)
        => db.QualityImages.FindAsync(new object[] { imageId }, ct).AsTask();

    public Task<bool> WarehouseLocationExistsAsync(int locationId, CancellationToken ct)
        => db.WarehouseLocations.AnyAsync(
            l => l.WarehouseLocationId == locationId
              && l.LocationStatus == "ACTIVE",
            ct);

    // ── Write ─────────────────────────────────────────────────────────────────

    public void AddInspection(QcInspection inspection)   => db.QcInspections.Add(inspection);
    public void AddSensoryResult(SensoryResult result)   => db.SensoryResults.Add(result);
    public void AddLabResult(LabResult result)            => db.LabResults.Add(result);
    public void AddImage(QualityImage image)              => db.QualityImages.Add(image);
    public void RemoveImage(QualityImage image)           => db.QualityImages.Remove(image);
    public void AddEnvironmentLog(EnvironmentLog log)     => db.EnvironmentLogs.Add(log);

    // ── List (UC62) ───────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<QcInspection> Items, int TotalCount)> ListAsync(
        string? batchCode,
        string? inspectionCode,
        string? status,
        string? qcResult,
        int? qcAccountId,
        DateTime? fromDate,
        DateTime? toDate,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = db.QcInspections
            .AsNoTracking()
            .Include(q => q.ProductBatch).ThenInclude(b => b.CropType)
            .Include(q => q.QcAccount)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(batchCode))
            query = query.Where(q => q.ProductBatch.BatchCode.Contains(batchCode));

        if (!string.IsNullOrWhiteSpace(inspectionCode))
            query = query.Where(q => q.InspectionCode.Contains(inspectionCode));

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(q => q.InspectionStatus == status.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(qcResult))
            query = query.Where(q => q.QcResult == qcResult.ToUpperInvariant());

        if (qcAccountId.HasValue)
            query = query.Where(q => q.QcAccountId == qcAccountId.Value);

        if (fromDate.HasValue)
            query = query.Where(q => q.StartedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(q => q.StartedAt <= toDate.Value);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(q => q.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct)
        => db.SaveChangesAsync(ct);

    public async Task<QcInspection> FinalizeAsync(long id, int actorAccountId, Action<QcInspection> evaluate, CancellationToken ct)
    {
        var attemptedCommit = false;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Finalization owns its unit of work. Reload after any rolled-back retry.
            db.ChangeTracker.Clear();
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'AccountID', @value={actorAccountId}", ct);
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var batchId = await db.QcInspections.Where(q => q.QcInspectionId == id).Select(q => (long?)q.ProductBatchId).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException($"Không tìm thấy phiếu kiểm định ID {id}.");
                var inspections = await db.QcInspections.FromSqlInterpolated($"SELECT * FROM QC_INSPECTION WITH (UPDLOCK,HOLDLOCK) WHERE ProductBatchID={batchId}")
                    .OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId).ToListAsync(ct);
                if (inspections[0].QcInspectionId != id) throw new ConflictException("Đã có phiếu QC mới hơn. Vui lòng tải lại lô.");
                await db.ProductBatches.FromSqlInterpolated($"SELECT * FROM PRODUCT_BATCH WITH (UPDLOCK,HOLDLOCK) WHERE ProductBatchID={batchId}").SingleAsync(ct);
                var inspection = (await GetInspectionDetailAsync(id, ct))!;
                if (attemptedCommit && inspection.InspectionStatus == "COMPLETED") return inspection;
                if (inspection.InspectionStatus != "COMPLETED" && inspection.ProductBatch.BatchStatus != "PENDING_QC")
                    throw new ConflictException("Trạng thái lô đã thay đổi. Không thể hoàn tất phiếu QC này.");
                var oldStatus = inspection.ProductBatch.BatchStatus;
                var previousHistory = await db.BatchStatusHistories.Where(h => h.ProductBatchId == batchId).MaxAsync(h => (long?)h.BatchStatusHistoryId, ct) ?? 0;
                evaluate(inspection);
                // Detail immutability trigger rejects writes after COMPLETED: persist evaluated
                // criteria first, then complete the header + batch, within the SAME transaction.
                var header = db.Entry(inspection); var batch = db.Entry(inspection.ProductBatch);
                var headerValues = header.CurrentValues.Clone(); var batchValues = batch.CurrentValues.Clone();
                header.CurrentValues.SetValues(header.OriginalValues); batch.CurrentValues.SetValues(batch.OriginalValues);
                header.State = EntityState.Unchanged; batch.State = EntityState.Unchanged;
                await db.SaveChangesAsync(ct);
                header.CurrentValues.SetValues(headerValues); batch.CurrentValues.SetValues(batchValues);
                await db.SaveChangesAsync(ct);
                if (!await db.BatchStatusHistories.AnyAsync(h => h.ProductBatchId == batchId && h.BatchStatusHistoryId > previousHistory && h.NewStatus == inspection.ProductBatch.BatchStatus, ct))
                {
                    db.BatchStatusHistories.Add(new BatchStatusHistory { ProductBatchId = batchId, OldStatus = oldStatus,
                        NewStatus = inspection.ProductBatch.BatchStatus, ChangedByAccountId = actorAccountId,
                        ChangedAt = DateTime.UtcNow, ChangeReason = "Hoàn tất QC " + inspection.InspectionCode });
                    await db.SaveChangesAsync(ct);
                }
                attemptedCommit = true;
                await tx.CommitAsync(ct);
                return inspection;
            }
            finally
            {
                if (db.Database.GetDbConnection().State == ConnectionState.Open)
                    await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL", CancellationToken.None);
                await db.Database.CloseConnectionAsync();
            }
        });
    }
}
