using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    private static async Task CancelResolutionOrderAsync(AppDbContext db, StockAdjustmentResolution r, int actorId, string reason, CancellationToken ct)
    {
        if (r.PurchaseOrderId is not { } orderId) return;
        var order = await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM PURCHASE_ORDER WITH (UPDLOCK,HOLDLOCK) WHERE PurchaseOrderID={orderId}").SingleAsync(ct);
        if (order.OrderStatus == "CANCELLED") return;
        if (!OpenOrderStatuses.Contains(order.OrderStatus)) throw new ConflictException("Đơn không còn ở trạng thái có thể hủy.");
        if (await db.InventoryReservations.AnyAsync(x => x.OrderDetail.PurchaseOrderId == orderId && (x.PickedQuantity > 0 || x.PickingHistories.Any()), ct)
            || await db.OrderDetails.AnyAsync(x => x.PurchaseOrderId == orderId && x.PickedWeightKg > 0, ct)
            || await db.GoodsIssues.AnyAsync(x => x.PurchaseOrderId == orderId && x.IssueStatus == "COMMITTED", ct))
            throw new ConflictException("Đã có soạn hàng hoặc xuất kho trên đơn; không thể hủy tự động.");
        var held = await db.InventoryReservations.Where(x => x.OrderDetail.PurchaseOrderId == orderId && PickingRules.ActiveReservations.Contains(x.ReservationStatus)).OrderBy(x => x.InventoryId).ThenBy(x => x.InventoryReservationId).ToListAsync(ct);
        foreach (var row in held) { row.ReservationStatus = "RELEASED"; row.ReleasedAt = DateTime.UtcNow; }
        await InvalidateDraftsAsync(db, r, orderId, actorId, reason, ct);
        order.OrderStatus = "CANCELLED"; order.CancellationReason = reason.Length > 1000 ? reason[..1000] : reason; order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); // SQL reservation sync owns all kg counters.
        var customer = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == orderId).Select(o => o.Distributor.AccountId).SingleAsync(ct);
        Notify(db, customer, r.StockAdjustmentId, orderId, "Đơn hàng đã hủy", reason + " Việc đối soát tiền thực hiện ngoài hệ thống.", true);
    }
    private static async Task InvalidateDraftsAsync(AppDbContext db, StockAdjustmentResolution r, long orderId, int actor, string reason, CancellationToken ct)
    {
        var drafts = await db.GoodsIssues.Where(i => i.PurchaseOrderId == orderId && i.IssueStatus == "DRAFT"
            && !db.GoodsIssueInvalidations.Any(v => v.GoodsIssueId == i.GoodsIssueId)).Select(i => i.GoodsIssueId).ToListAsync(ct);
        foreach (var id in drafts) db.GoodsIssueInvalidations.Add(new GoodsIssueInvalidation { GoodsIssueId = id,
            StockAdjustmentId = r.StockAdjustmentId, AccountId = actor, InvalidatedAt = DateTime.UtcNow, Reason = reason });
    }
    private static async Task FinalizeAcceptedOrderAsync(AppDbContext db, StockAdjustmentResolution r, int actorId, CancellationToken ct)
    {
        var proposal = await db.StockAdjustmentProposals.SingleOrDefaultAsync(p => p.StockAdjustmentId == r.StockAdjustmentId && p.ProposalStatus == "ACCEPTED", ct)
            ?? throw new ConflictException("Khách chưa đồng ý phiên bản đề xuất hiện hành.");
        await EnsureCountScopeAsync(db, r, proposal, ct);
        var countScope = await CountScopeAsync(db, r, ct); var scopeIds = countScope.Select(x => x.StockAdjustmentId).ToArray();
        var order = await db.PurchaseOrders.SingleAsync(o => o.PurchaseOrderId == r.PurchaseOrderId, ct);
        if (order.OrderStatus is not ("APPROVED" or "RESERVED") || order.ApprovedAt == null)
            throw new ConflictException("Đơn chưa được duyệt hoặc đã hủy; không thể tiếp tục phân bổ.");
        if (await db.StockAdjustmentResolutions.AnyAsync(x => x.PurchaseOrderId == order.PurchaseOrderId && !scopeIds.Contains(x.StockAdjustmentId)
            && x.State != "RESOLVED" && x.State != "CLEARED" && x.State != "REPLACED", ct))
            throw new ConflictException("Đơn còn phiếu xử lý khác; cần rà soát toàn bộ trước khi tiếp tục.");
        var line = await db.OrderDetails.SingleAsync(l => l.OrderDetailId == r.OrderDetailId, ct);
        var batch = r.StockAdjustment.Inventory.ProductBatch;
        if (PickingRules.BatchLockReason(batch.BatchStatus, batch.ExpiryDate) != null)
            throw new ConflictException("Lô hết hạn hoặc bị chặn; không thể tiếp tục đơn.");
        if (await BatchHandlingEligibility.CompletedQcReasonAsync(db, batch.ProductBatchId, batch.QualityGrade, ct) is { } qcReason) throw new ConflictException(qcReason);
        var stock = await db.Inventories.Include(i => i.WarehouseLocation).Where(i => i.ProductBatchId == batch.ProductBatchId).OrderBy(i => i.InventoryId).ToListAsync(ct);
        if (stock.Any(i => i.Unit != "kg" || i.WarehouseLocation.LocationStatus != "ACTIVE")) throw new ConflictException("Vị trí hoặc đơn vị tồn không hợp lệ.");
        if (stock.Sum(i => i.QuantityOnHand) - countScope.Sum(x => x.StockAdjustment.SystemQuantity - x.StockAdjustment.ActualQuantity) != proposal.ProposedWeightKg)
            throw new ConflictException("Khối lượng thực tế không còn khớp đề xuất đã đồng ý.");
        var reservations = await db.InventoryReservations.Where(x => x.OrderDetailId == line.OrderDetailId).ToListAsync(ct);
        if (reservations.Any(x => x.PickedQuantity != 0 || x.PickingHistories.Any())) throw new ConflictException("Đã có soạn hàng; không được thay giữ hàng.");
        foreach (var row in reservations) { row.ReservationStatus = "RELEASED"; row.ReleasedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        foreach (var item in stock) await db.Entry(item).ReloadAsync(ct);
        if (stock.Any(i => i.ReservedQuantity != 0)) throw new ConflictException("Lô còn phần giữ hàng của đơn khác.");
        await InvalidateDraftsAsync(db, r, order.PurchaseOrderId, actorId, "Đơn đã điều chỉnh theo đề xuất khách đồng ý; cần lập lại phiếu xuất nháp.", ct);
        db.OrderAmendments.Add(new OrderAmendment { StockAdjustmentProposalId = proposal.StockAdjustmentProposalId, OrderDetailId = line.OrderDetailId,
            PreviousWeightKg = line.ApprovedWeightKg, EffectiveWeightKg = proposal.ProposedWeightKg, PreviousWholeLotPrice = line.UnitPrice,
            EffectiveWholeLotPrice = proposal.ProposedWholeLotPrice, PreviousOrderTotal = order.TotalAmount, EffectiveOrderTotal = proposal.ProposedOrderTotal,
            ApprovedByAccountId = actorId, ApprovedAt = DateTime.UtcNow });
        line.ApprovedWeightKg = proposal.ProposedWeightKg; line.UnitPrice = proposal.ProposedWholeLotPrice;
        order.SubtotalAmount = proposal.ProposedOrderTotal - order.TaxAmount; order.TotalAmount = proposal.ProposedOrderTotal; order.UpdatedAt = DateTime.UtcNow;
        // Save weight before reservation sync enforces its ceiling. RequestedWeightKg remains the original contract.
        await db.SaveChangesAsync(ct);
        foreach (var member in countScope) { var counted = stock.Single(i => i.InventoryId == member.StockAdjustment.InventoryId);
            counted.QuantityOnHand = member.StockAdjustment.ActualQuantity; counted.LastUpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        foreach (var item in stock.Where(i => i.QuantityOnHand > 0)) {
            var row = reservations.SingleOrDefault(x => x.InventoryId == item.InventoryId);
            if (row == null) { row = new InventoryReservation { OrderDetailId = line.OrderDetailId, InventoryId = item.InventoryId }; db.InventoryReservations.Add(row); }
            row.ReservedQuantity = item.QuantityOnHand; row.PickedQuantity = 0; row.ReservationStatus = "RESERVED"; row.ReleasedAt = null; row.ReservedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        var allLines = await db.OrderDetails.AsNoTracking().Where(l => l.PurchaseOrderId == order.PurchaseOrderId).ToListAsync(ct);
        if (allLines.Any(l => l.ApprovedWeightKg <= 0 || l.ReservedWeightKg != l.ApprovedWeightKg))
            throw new ConflictException("Chưa phân bổ đủ các dòng của đơn; toàn bộ quyết định được hoàn tác.");
        order.OrderStatus = "RESERVED"; proposal.ProposalStatus = "FINALIZED";
        var customer = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == order.PurchaseOrderId).Select(o => o.Distributor.AccountId).SingleAsync(ct);
        Notify(db, customer, r.StockAdjustmentId, order.PurchaseOrderId, "Đơn đã được điều chỉnh", "Khối lượng và giá mới đã có hiệu lực sau phê duyệt kho. Đối soát tiền thực hiện ngoài hệ thống.", true, "STOCK_ADJUSTMENT_REVIEW");
    }
}
