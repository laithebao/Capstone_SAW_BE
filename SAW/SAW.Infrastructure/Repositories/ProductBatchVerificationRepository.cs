using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class ProductBatchVerificationRepository(
    AppDbContext dbContext, IHttpContextAccessor httpContextAccessor) : IProductBatchVerificationRepository
{
    public Task<bool> ConfirmAsync(long batchId, int supplierId, VerifiedReceivingDetails details,
        BatchStatusHistory history, CancellationToken cancellationToken) =>
        TransitionAsync(batchId, supplierId, "PENDING_QC", details, null, history, cancellationToken);

    public Task<bool> RejectAsync(long batchId, int supplierId, string reason,
        BatchStatusHistory history, CancellationToken cancellationToken) =>
        TransitionAsync(batchId, supplierId, "REJECTED", null, reason, history, cancellationToken);

    private Task<bool> TransitionAsync(long batchId, int supplierId, string nextStatus,
        VerifiedReceivingDetails? details, string? reason, BatchStatusHistory history,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var prior = await dbContext.ProductBatches.AsNoTracking()
                .SingleOrDefaultAsync(b => b.ProductBatchId == batchId &&
                    b.SupplierId == supplierId, cancellationToken);
            if (prior is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            var previousHistoryId = await dbContext.BatchStatusHistories
                .Where(h => h.ProductBatchId == batchId)
                .Select(h => (long?)h.BatchStatusHistoryId)
                .MaxAsync(cancellationToken) ?? 0;

            int affected;
            try
            {
                // The status trigger reads AccountID on this connection. Clear it before pooling.
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"EXEC sys.sp_set_session_context @key=N'AccountID', @value={history.ChangedByAccountId}",
                    cancellationToken);
                var candidates = dbContext.ProductBatches.Where(b =>
                    b.ProductBatchId == batchId && b.SupplierId == supplierId &&
                    b.BatchStatus == "SUBMITTED" && b.VerifiedQuantity == null &&
                    b.VerifiedWeightInKg == null);
                if (details is not null)
                {
                    affected = await candidates.ExecuteUpdateAsync(setters => setters
                        .SetProperty(b => b.VerifiedQuantity, details.VerifiedQuantity)
                        .SetProperty(b => b.VerifiedWeightInKg, details.VerifiedWeightInKg)
                        .SetProperty(b => b.VerifiedPackagingType, details.VerifiedPackagingType)
                        .SetProperty(b => b.VerifiedPackageCount, details.VerifiedPackageCount)
                        .SetProperty(b => b.VerifiedPackageUnitWeightKg, details.VerifiedPackageUnitWeightKg)
                        .SetProperty(b => b.ReceivingNote, details.ReceivingNote)
                        .SetProperty(b => b.BatchStatus, nextStatus)
                        .SetProperty(b => b.UpdatedAt, history.ChangedAt), cancellationToken);
                }
                else
                {
                    affected = await candidates.ExecuteUpdateAsync(setters => setters
                        .SetProperty(b => b.RejectionReason, reason)
                        .SetProperty(b => b.BatchStatus, nextStatus)
                        .SetProperty(b => b.UpdatedAt, history.ChangedAt), cancellationToken);
                }
            }
            finally
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL",
                    CancellationToken.None);
            }

            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // A trigger-created history row is immutable. Add a fallback only if it is absent.
            var triggered = await dbContext.BatchStatusHistories.AnyAsync(h =>
                h.ProductBatchId == batchId && h.BatchStatusHistoryId > previousHistoryId &&
                h.OldStatus == "SUBMITTED" && h.NewStatus == nextStatus, cancellationToken);
            if (!triggered) dbContext.BatchStatusHistories.Add(history);

            if (details is null)
                AddAudit(batchId, history.ChangedByAccountId!.Value, history.ChangedAt,
                    "Product batch rejected at receiving.",
                    new { BatchStatus = prior.BatchStatus, prior.RejectionReason },
                    new { BatchStatus = nextStatus, RejectionReason = reason });
            else
                AddAudit(batchId, history.ChangedByAccountId!.Value, history.ChangedAt,
                    "Physical verification completed; product batch submitted for QC.",
                    new
                    {
                        BatchStatus = prior.BatchStatus, prior.VerifiedQuantity,
                        prior.VerifiedWeightInKg, prior.VerifiedPackagingType,
                        prior.VerifiedPackageCount, prior.VerifiedPackageUnitWeightKg,
                        prior.ReceivingNote
                    },
                    new
                    {
                        BatchStatus = nextStatus, details.VerifiedQuantity, details.VerifiedWeightInKg,
                        details.VerifiedPackagingType, details.VerifiedPackageCount,
                        details.VerifiedPackageUnitWeightKg, details.ReceivingNote
                    });
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    public Task<bool> UpdateAsync(long batchId, DateTime? expectedUpdatedAt,
        DateTime expectedCreatedAt, int accountId,
        VerifiedReceivingDetails details, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var current = await dbContext.ProductBatches.AsNoTracking()
                .SingleOrDefaultAsync(b => b.ProductBatchId == batchId, cancellationToken);
            if (current is null || current.BatchStatus != "PENDING_QC" ||
                current.CreatedAt != expectedCreatedAt ||
                current.UpdatedAt != expectedUpdatedAt)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // UpdatedAt is datetime2(0) in the existing DB. Advance the stored second
            // so another staff member cannot reuse an old concurrency token.
            var utcNow = DateTime.UtcNow;
            var nextUpdatedAt = new DateTime(
                utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            if (expectedUpdatedAt.HasValue && nextUpdatedAt <= expectedUpdatedAt.Value)
                nextUpdatedAt = expectedUpdatedAt.Value.AddSeconds(1);

            var affected = await dbContext.ProductBatches
                .Where(b => b.ProductBatchId == batchId && b.BatchStatus == "PENDING_QC" &&
                    b.CreatedAt == expectedCreatedAt &&
                    b.UpdatedAt == expectedUpdatedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(b => b.VerifiedQuantity, details.VerifiedQuantity)
                    .SetProperty(b => b.VerifiedWeightInKg, details.VerifiedWeightInKg)
                    .SetProperty(b => b.VerifiedPackagingType, details.VerifiedPackagingType)
                    .SetProperty(b => b.VerifiedPackageCount, details.VerifiedPackageCount)
                    .SetProperty(b => b.VerifiedPackageUnitWeightKg, details.VerifiedPackageUnitWeightKg)
                    .SetProperty(b => b.ReceivingNote, details.ReceivingNote)
                    .SetProperty(b => b.UpdatedAt, nextUpdatedAt), cancellationToken);
            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            AddAudit(batchId, accountId, nextUpdatedAt, "Receiving details corrected.",
                new
                {
                    current.VerifiedQuantity, current.VerifiedWeightInKg,
                    current.VerifiedPackagingType, current.VerifiedPackageCount,
                    current.VerifiedPackageUnitWeightKg, current.ReceivingNote,
                    current.UpdatedAt
                }, new
                {
                    details.VerifiedQuantity, details.VerifiedWeightInKg,
                    details.VerifiedPackagingType, details.VerifiedPackageCount,
                    details.VerifiedPackageUnitWeightKg, details.ReceivingNote,
                    UpdatedAt = nextUpdatedAt
                });
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    private void AddAudit(long batchId, int accountId, DateTime now,
        string description, object oldData, object newData)
    {
        var context = httpContextAccessor.HttpContext;
        dbContext.AuditLogs.Add(new AuditLog
        {
            AccountId = accountId,
            ActionType = "UPDATE",
            EntityName = "PRODUCT_BATCH",
            EntityId = $"ProductBatchId={batchId}",
            OldDataJson = JsonSerializer.Serialize(oldData),
            NewDataJson = JsonSerializer.Serialize(newData),
            Description = description,
            IpAddress = Limit(context?.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent = Limit(context?.Request.Headers.UserAgent.ToString(), 500),
            CreatedAt = now
        });
    }

    private static string? Limit(string? value, int maxLength) =>
        value is null ? null : value[..Math.Min(value.Length, maxLength)];
}
