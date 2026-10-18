using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    private static readonly string[] OpenOrderStatuses = ["PENDING", "APPROVED", "RESERVED", "PARTIALLY_PICKED", "PICKED", "PREPARED", "PARTIALLY_ISSUED", "ISSUED", "DISPATCHED"];
    private static async Task<List<AdjustmentRelatedOrder>> RelatedOrdersAsync(AppDbContext db, long batchId, CancellationToken ct) =>
        await db.OrderDetails.AsNoTracking().Where(l => OpenOrderStatuses.Contains(l.PurchaseOrder.OrderStatus)
            && (l.RequestedProductBatchId == batchId || l.InventoryReservations.Any(r => r.Inventory.ProductBatchId == batchId && PickingRules.ActiveReservations.Contains(r.ReservationStatus))))
            .OrderBy(l => l.PurchaseOrderId).ThenBy(l => l.OrderDetailId)
            .Select(l => new AdjustmentRelatedOrder(l.PurchaseOrderId, l.OrderDetailId, l.PurchaseOrder.OrderCode, l.PurchaseOrder.OrderStatus,
                l.ApprovedWeightKg > 0 ? l.ApprovedWeightKg : l.RequestedWeightKg, l.UnitPrice)).ToListAsync(ct);

    private static async Task<string?> OrderScopeReasonAsync(AppDbContext db, long batchId, List<AdjustmentRelatedOrder> orders, CancellationToken ct)
    {
        if (orders.Count == 0) return await db.InventoryReservations.AnyAsync(r => r.Inventory.ProductBatchId == batchId
            && PickingRules.ActiveReservations.Contains(r.ReservationStatus), ct) ? "Phần giữ hàng không liên kết được với đơn nguyên lô hợp lệ; cần rà soát." : null;
        if (orders.Count != 1) return "Lô liên quan nhiều dòng/đơn đang hoạt động. Cần rà soát, không thể tự động quyết định.";
        var linked = orders.Single();
        var line = await db.OrderDetails.AsNoTracking().SingleAsync(l => l.OrderDetailId == linked.LineId, ct);
        if (line.RequestedProductBatchId != batchId || line.Unit != "Lô" || line.RequestedQuantity != 1 || line.UnitPrice <= 0)
            return "Quan hệ đơn và lô hoặc đơn vị bán nguyên lô không hợp lệ.";
        if (await db.InventoryReservations.AnyAsync(r => r.OrderDetail.PurchaseOrderId == linked.Id
            && (r.Inventory.ProductBatchId != r.OrderDetail.RequestedProductBatchId || r.PickedQuantity > 0 || r.PickingHistories.Any()), ct)
            || await db.GoodsIssues.AnyAsync(i => i.PurchaseOrderId == linked.Id && i.IssueStatus == "COMMITTED", ct)
            || await db.OrderDetails.AnyAsync(l => l.PurchaseOrderId == linked.Id && l.PickedWeightKg > 0, ct))
            return "Đơn đã có soạn hàng hoặc xuất kho. Luồng này chỉ áp dụng trước lần soạn đầu tiên của toàn đơn.";
        return null;
    }
    private static async Task<string> OrderSnapshotAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Where(o => o.PurchaseOrderId == id).Select(o => new { o.PurchaseOrderId, o.OrderStatus,
            o.TotalAmount, o.SubtotalAmount, o.UpdatedAt, o.DistributorId, o.CancellationReason }).SingleAsync(ct);
        var lines = await db.OrderDetails.AsNoTracking().Where(l => l.PurchaseOrderId == id).OrderBy(l => l.OrderDetailId)
            .Select(l => new { l.OrderDetailId, l.RequestedProductBatchId, l.RequestedWeightKg, l.ApprovedWeightKg, l.UnitPrice, l.ReservedWeightKg, l.PickedWeightKg }).ToListAsync(ct);
        var reservations = await db.InventoryReservations.AsNoTracking().Where(r => r.OrderDetail.PurchaseOrderId == id).OrderBy(r => r.InventoryReservationId)
            .Select(r => new { r.InventoryReservationId, r.InventoryId, r.OrderDetailId, r.ReservedQuantity, r.PickedQuantity, r.ReservationStatus, r.ReleasedAt }).ToListAsync(ct);
        return JsonSerializer.Serialize(new { order, lines, reservations });
    }
    private static async Task<AdjustmentResolutionDetail?> ResolutionDetailAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var r = await db.StockAdjustmentResolutions.AsNoTracking().Include(x => x.PurchaseOrder).Include(x => x.OrderDetail)
            .Include(x => x.Proposals).Include(x => x.StockAdjustment).ThenInclude(a => a.Inventory).ThenInclude(i => i.ProductBatch).SingleOrDefaultAsync(x => x.StockAdjustmentId == id, ct);
        if (r is null) return null;
        AdjustmentRelatedOrder? order = r.PurchaseOrder is { } o && r.OrderDetail is { } l
            ? new(o.PurchaseOrderId, l.OrderDetailId, o.OrderCode, o.OrderStatus, l.ApprovedWeightKg > 0 ? l.ApprovedWeightKg : l.RequestedWeightKg, l.UnitPrice) : null;
        var scope = await CountScopeAsync(db, r, ct);
        var proposals = r.CoordinatingStockAdjustmentId is { } parent && parent != id
            ? await db.StockAdjustmentProposals.AsNoTracking().Where(p => p.StockAdjustmentId == parent).ToListAsync(ct) : r.Proposals.ToList();
        var auditEntity = (r.CoordinatingStockAdjustmentId ?? id).ToString();
        var actor = await db.AuditLogs.AsNoTracking().Where(l => l.EntityName == "STOCK_ADJUSTMENT" && l.EntityId == auditEntity && l.AccountId != null)
            .OrderByDescending(l => l.AuditLogId).Select(l => l.Account!.FullName).FirstOrDefaultAsync(ct);
        var replacement = await db.StockAdjustmentResolutions.AsNoTracking().Where(x => x.ReplacesStockAdjustmentId == id)
            .Select(x => new { Id = x.StockAdjustmentId, Name = x.StockAdjustment.CreatedByAccount.FullName }).SingleOrDefaultAsync(ct);
        return new(r.State, r.Version, order, proposals.OrderByDescending(p => p.Revision).Select(p => new AdjustmentProposalDetail(p.StockAdjustmentProposalId,
            p.Revision, p.ProposalStatus, p.OriginalWeightKg, p.ProposedWeightKg, p.OriginalWholeLotPrice, p.ProposedWholeLotPrice,
            p.OriginalOrderTotal, p.ProposedOrderTotal, p.CustomerMessage, p.SentAt, p.RespondedAt, p.ResponseReason, p.WithdrawalReason)).ToList(),
            r.State is "RESOLVED" or "CLEARED" or "REPLACED" ? null : "Lô và đơn liên quan đang tạm dừng xử lý điều chỉnh tồn.", r.ReplacesStockAdjustmentId, r.StockAdjustmentId, r.CoordinatingStockAdjustmentId, CountValues(scope), r.ResolutionReason, r.UpdatedAt, replacement?.Name ?? actor, replacement?.Id, r.StockAdjustment.Inventory.ProductBatch.BatchCode, r.StockAdjustment.Inventory.ProductBatch.ProductName);
    }
    private static async Task EnsureAuthorityAsync(AppDbContext db, int actorId, string role, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(a => a.AccountId == actorId && a.AccountStatus == "ACTIVE" && a.Role.IsActive && a.Role.RoleCode == role, ct))
            throw new ForbiddenException("Tài khoản không có quyền thực hiện thao tác này.");
    }
    private static void Notify(AppDbContext db, int accountId, long adjustmentId, long? orderId, string title, string message, bool customer = false, string notificationType = "STOCK_ADJUSTMENT") =>
        db.Notifications.Add(new Notification { AccountId = accountId, NotificationType = notificationType, Priority = "HIGH", Title = title,
            Message = message, RelatedEntityType = customer ? "DISTRIBUTOR_ORDER" : "STOCK_ADJUSTMENT", RelatedEntityId = (customer ? orderId : adjustmentId)?.ToString(), CreatedAt = DateTime.UtcNow });
    private static async Task NotifyWarehouseAsync(AppDbContext db, long id, string title, string message, CancellationToken ct, bool managersOnly = false)
    {
        foreach (var account in await db.Accounts.Where(a => a.AccountStatus == "ACTIVE" && a.Role.IsActive
            && (a.Role.RoleCode == "WAREHOUSE_MANAGER" || !managersOnly && a.Role.RoleCode == "OPERATION_STAFF")).Select(a => a.AccountId).ToListAsync(ct))
            Notify(db, account, id, null, title, message, notificationType: managersOnly ? "STOCK_ADJUSTMENT_CUSTOMER_RESPONSE" : "STOCK_ADJUSTMENT");
    }
    private static void RecordResolution(AppDbContext db, long id, int actor, string action, Guid key, string payload, string before, object values) =>
        db.AuditLogs.Add(new AuditLog { AccountId = actor, EntityName = "STOCK_ADJUSTMENT", EntityId = id.ToString(), ActionType = "RESOLUTION_" + action,
            CreatedAt = DateTime.UtcNow, Description = "Xử lý phiếu điều chỉnh tồn: " + action, OldDataJson = before,
            NewDataJson = JsonSerializer.Serialize(new { RequestId = key, PayloadHash = payload, Values = values }) });
    private static async Task<bool> ReplayAsync(AppDbContext db, int actor, Guid key, string payload, CancellationToken ct)
    {
        if (key == Guid.Empty) throw new BadRequestException("Thiếu mã thao tác.");
        var resource = "adjustment-resolution:" + key;
        var locks = await db.Database.SqlQuery<int>($"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource={resource},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r AS Value;").ToListAsync(ct);
        if (locks.Single() < 0) throw new ConflictException("Thao tác đang được xử lý.");
        var marker = key.ToString();
        var logs = await db.AuditLogs.AsNoTracking().Where(a => a.ActionType.StartsWith("RESOLUTION_") && a.NewDataJson != null && a.NewDataJson.Contains(marker)).ToListAsync(ct);
        foreach (var log in logs) {
            using var json = JsonDocument.Parse(log.NewDataJson!);
            if (json.RootElement.GetProperty("RequestId").GetGuid() != key) continue;
            if (log.AccountId != actor || json.RootElement.GetProperty("PayloadHash").GetString() != payload)
                throw new ConflictException("Mã thao tác đã dùng với dữ liệu khác.");
            return true;
        }
        return false;
    }
}
