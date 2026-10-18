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

    internal static IQueryable<long> PendingHandlingBatches(AppDbContext db) => db.StockAdjustments
        .Where(a => a.AdjustmentStatus == "PENDING" || a.Resolution != null
            && a.Resolution.State != "RESOLVED" && a.Resolution.State != "CLEARED" && a.Resolution.State != "REPLACED")
        .Select(a => a.Inventory.ProductBatchId);
    internal static IQueryable<long> HeldOrders(AppDbContext db) => db.PurchaseOrders.Where(o =>
        db.StockAdjustmentResolutions.Any(r => r.PurchaseOrderId == o.PurchaseOrderId && r.State != "RESOLVED" && r.State != "CLEARED" && r.State != "REPLACED")
        || o.OrderDetails.Any(l => l.RequestedProductBatchId != null && PendingHandlingBatches(db).Contains(l.RequestedProductBatchId.Value)
            || l.InventoryReservations.Any(r => r.ReleasedAt == null && (r.ReservationStatus == "RESERVED" || r.ReservationStatus == "PARTIALLY_PICKED" || r.ReservationStatus == "PICKED")
                && PendingHandlingBatches(db).Contains(r.Inventory.ProductBatchId))))
        .Select(o => o.PurchaseOrderId);
    internal static Task<bool> OrderHeldAsync(AppDbContext db, long orderId, CancellationToken ct) => HeldOrders(db).ContainsAsync(orderId, ct);
    internal static async Task<string?> HandlingReasonAsync(AppDbContext db, long batchId, CancellationToken ct)
    {
        if (await PendingDisposalBatches(db).ContainsAsync(batchId, ct)) return StockAdjustmentRules.DisposalLockReason;
        return await PendingHandlingBatches(db).ContainsAsync(batchId, ct) ? StockAdjustmentRules.CountLockReason : null;
    }
    internal static async Task<string?> CompletedQcReasonAsync(AppDbContext db, long batchId, string? grade, CancellationToken ct) =>
        await db.QcInspections.AsNoTracking().Where(q => q.ProductBatchId == batchId).OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId).Take(1)
            .AnyAsync(q => q.InspectionStatus == "COMPLETED" && q.CompletedAt >= q.StartedAt && q.QcResult == "PASS" && q.QualityGrade == grade
                && (q.QualityGrade == "A" || q.QualityGrade == "B" || q.QualityGrade == "C" || q.QualityGrade == "D" || q.QualityGrade == "E"), ct)
            ? null : "Kết quả QC mới nhất không đủ điều kiện tiếp tục đơn; cần kho kiểm tra lại.";

    internal static async Task ValidateAdjustedSaleAsync(AppDbContext db, long batchId, decimal expectedWeight, CancellationToken ct)
    {
        var offer = await db.BatchSaleOffers.AsNoTracking().SingleOrDefaultAsync(o => o.ProductBatchId == batchId, ct);
        if (offer?.SourceStockAdjustmentId is not { } source) return; // Preserve ordinary whole-lot policy.
        if (offer.RepricedForAdjustmentId != source || offer.ApprovedRemainderWeightKg != expectedWeight
            || !await db.StockAdjustmentResolutions.AnyAsync(r => r.StockAdjustmentId == source && r.State == "RESOLVED" && r.ResolvedBatchWeightKg == expectedWeight
                && r.StockAdjustment.RequestKind == "COUNT" && r.StockAdjustment.AdjustmentStatus == "APPROVED" && r.StockAdjustment.Inventory.ProductBatchId == batchId, ct)
            || await db.Inventories.Where(i => i.ProductBatchId == batchId).SumAsync(i => i.QuantityOnHand, ct) != expectedWeight)
            throw new SAW.Application.Exceptions.ConflictException("Nguồn phê duyệt phần lô còn lại hoặc giá/khối lượng không còn hợp lệ.");
        var batch = await db.ProductBatches.AsNoTracking().Include(b => b.CropType).SingleAsync(b => b.ProductBatchId == batchId, ct);
        if (batch.BatchStatus != "IN_STOCK" || !batch.CropType.IsActive || batch.ExpiryDate < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7))
            || await db.Inventories.AnyAsync(i => i.ProductBatchId == batchId && (i.Unit != "kg" || i.WarehouseLocation.LocationStatus != "ACTIVE"), ct))
            throw new SAW.Application.Exceptions.ConflictException("Phần lô còn lại không còn đủ điều kiện kho để phân bổ.");
        var grade = batch.QualityGrade;
        if (await CompletedQcReasonAsync(db, batchId, grade, ct) is { } reason) throw new SAW.Application.Exceptions.ConflictException(reason);
    }
}
