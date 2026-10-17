using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.StockAdjustments;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

internal static class BatchHandlingEligibility
{
    internal static IQueryable<long> PendingDisposalBatches(AppDbContext db) => db.StockAdjustments
        .Where(a => a.RequestKind == "DISPOSAL" && a.AdjustmentStatus == "PENDING").Select(a => a.Inventory.ProductBatchId);
    internal static async Task<string?> DisposalReasonAsync(AppDbContext db, long batchId, CancellationToken ct) =>
        await PendingDisposalBatches(db).ContainsAsync(batchId, ct) ? StockAdjustmentRules.DisposalLockReason : null;
}
