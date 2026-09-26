using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class ProductBatchVerificationRepository(
    AppDbContext dbContext, IHttpContextAccessor httpContextAccessor) : IProductBatchVerificationRepository
{
    public Task<bool> ConfirmAsync(long batchId, int supplierId, decimal verifiedQuantity,
        decimal verifiedWeightInKg, BatchStatusHistory history, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var previousHistoryId = await dbContext.BatchStatusHistories
                .Where(h => h.ProductBatchId == batchId)
                .Select(h => (long?)h.BatchStatusHistoryId)
                .MaxAsync(cancellationToken) ?? 0;
            // The database status-history trigger reads AccountID from this connection.
            // Clear it before the connection can return to the pool.
            int affected;
            try
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"EXEC sys.sp_set_session_context @key=N'AccountID', @value={history.ChangedByAccountId}",
                    cancellationToken);
                affected = await dbContext.ProductBatches
                    .Where(b => b.ProductBatchId == batchId && b.SupplierId == supplierId &&
                        b.BatchStatus == "SUBMITTED" && b.VerifiedQuantity == null &&
                        b.VerifiedWeightInKg == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(b => b.VerifiedQuantity, verifiedQuantity)
                        .SetProperty(b => b.VerifiedWeightInKg, verifiedWeightInKg)
                        .SetProperty(b => b.BatchStatus, "PENDING_QC")
                        .SetProperty(b => b.UpdatedAt, history.ChangedAt), cancellationToken);
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

            // The trigger's history row is immutable on existing databases. Add a row
            // only when no trigger created one.
            var triggeredHistory = await dbContext.BatchStatusHistories
                .Where(h => h.ProductBatchId == batchId && h.BatchStatusHistoryId > previousHistoryId &&
                    h.OldStatus == "SUBMITTED" &&
                    h.NewStatus == "PENDING_QC")
                .OrderByDescending(h => h.BatchStatusHistoryId)
                .FirstOrDefaultAsync(cancellationToken);
            if (triggeredHistory is null)
                dbContext.BatchStatusHistories.Add(history);
            var context = httpContextAccessor.HttpContext;
            dbContext.AuditLogs.Add(new AuditLog
            {
                AccountId = history.ChangedByAccountId,
                ActionType = "UPDATE",
                EntityName = "PRODUCT_BATCH",
                EntityId = $"ProductBatchId={batchId}",
                OldDataJson = JsonSerializer.Serialize(new
                {
                    BatchStatus = "SUBMITTED",
                    VerifiedQuantity = (decimal?)null,
                    VerifiedWeightInKg = (decimal?)null
                }),
                NewDataJson = JsonSerializer.Serialize(new
                {
                    BatchStatus = "PENDING_QC",
                    VerifiedQuantity = verifiedQuantity,
                    VerifiedWeightInKg = verifiedWeightInKg
                }),
                Description = "Physical verification completed; product batch submitted for QC.",
                IpAddress = Limit(context?.Connection.RemoteIpAddress?.ToString(), 64),
                UserAgent = Limit(context?.Request.Headers.UserAgent.ToString(), 500),
                CreatedAt = history.ChangedAt
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    private static string? Limit(string? value, int maxLength) =>
        value is null ? null : value[..Math.Min(value.Length, maxLength)];
}
