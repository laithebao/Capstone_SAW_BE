using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class PickingRepository(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor) : IPickingRepository
{
    private AppDbContext Context() => new(options, accessor);
    public async Task<PickingPage<PickingOrderSummary>> SearchAsync(PickingQuery query, CancellationToken ct)
    {
        await using var db = Context();
        var rows = db.PurchaseOrders.AsNoTracking().Where(o => PickingRules.WorkStatuses.Contains(o.OrderStatus)
            && o.ApprovedAt != null && o.ApprovedByAccountId != null
            && (!o.OrderDetails.Any(l => l.InventoryReservations.Any(r => PickingRules.ActiveReservations.Contains(r.ReservationStatus)))
                || o.OrderDetails.Any(l => l.InventoryReservations.Any(r => PickingRules.ActiveReservations.Contains(r.ReservationStatus)
                    && r.PickedQuantity < r.ReservedQuantity))
                || o.OrderDetails.Any(l => l.ApprovedWeightKg > (l.InventoryReservations.Where(r => PickingRules.AllocatedReservations.Contains(r.ReservationStatus))
                    .Sum(r => (decimal?)r.ReservedQuantity) ?? 0))));
        if (query.Status is not null) rows = rows.Where(o => o.OrderStatus == query.Status);
        if (query.Search is { } term) rows = rows.Where(o => o.OrderCode.Contains(term) || o.Distributor.DistributorName.Contains(term)
            || o.OrderDetails.Any(l => l.CropType.CropName.Contains(term)
                || (l.RequestedProductBatch != null && (l.RequestedProductBatch.BatchCode.Contains(term) || l.RequestedProductBatch.ProductName.Contains(term)))
                || l.InventoryReservations.Any(r => r.Inventory.ProductBatch.BatchCode.Contains(term)
                || r.Inventory.ProductBatch.ProductName.Contains(term))));
        if (query.FromDate is { } from) { var date = from.ToDateTime(TimeOnly.MinValue).AddHours(-7); rows = rows.Where(o => o.ApprovedAt >= date); }
        if (query.ToDate is { } to) { var date = to.ToDateTime(TimeOnly.MinValue).AddDays(1).AddHours(-7); rows = rows.Where(o => o.ApprovedAt < date); }
        var count = await rows.CountAsync(ct);
        var ordered = query.Sort switch
        {
            "approved_asc" => rows.OrderBy(o => o.ApprovedAt).ThenBy(o => o.PurchaseOrderId),
            "delivery_asc" => rows.OrderBy(o => o.ExpectedDeliveryDate == null).ThenBy(o => o.ExpectedDeliveryDate).ThenBy(o => o.PurchaseOrderId),
            "delivery_desc" => rows.OrderBy(o => o.ExpectedDeliveryDate == null).ThenByDescending(o => o.ExpectedDeliveryDate).ThenByDescending(o => o.PurchaseOrderId),
            _ => rows.OrderByDescending(o => o.ApprovedAt).ThenByDescending(o => o.PurchaseOrderId)
        };
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        // One bounded summary query: list requests do not load full picking histories for every order.
        var summariesData = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(o => new
        {
            Id = o.PurchaseOrderId, o.OrderCode, o.Distributor.DistributorName, o.ApprovedAt, o.ExpectedDeliveryDate, o.OrderStatus,
            Approved = o.OrderDetails.Sum(l => (decimal?)l.ApprovedWeightKg) ?? 0,
            Reserved = o.OrderDetails.SelectMany(l => l.InventoryReservations).Where(r => PickingRules.AllocatedReservations.Contains(r.ReservationStatus)).Sum(r => (decimal?)r.ReservedQuantity) ?? 0,
            Picked = o.OrderDetails.SelectMany(l => l.InventoryReservations).Where(r => PickingRules.AllocatedReservations.Contains(r.ReservationStatus)).Sum(r => (decimal?)r.PickedQuantity) ?? 0,
            FullyAllocated = o.OrderDetails.Any() && !o.OrderDetails.Any(l => l.ApprovedWeightKg <= 0 || l.ApprovedWeightKg !=
                (l.InventoryReservations.Where(r => PickingRules.AllocatedReservations.Contains(r.ReservationStatus)).Sum(r => (decimal?)r.ReservedQuantity) ?? 0)),
            HasPickable = !BatchHandlingEligibility.HeldOrders(db).Contains(o.PurchaseOrderId) && o.OrderDetails.Any(l => l.RequestedProductBatchId != null
                && l.RequestedProductBatch!.Inventories.Sum(i => (decimal?)i.QuantityOnHand) == l.ApprovedWeightKg
                && !l.InventoryReservations.Any(p => p.ReleasedAt == null && PickingRules.ActiveReservations.Contains(p.ReservationStatus)
                    && (p.Inventory.Unit != "kg" || p.Inventory.WarehouseLocation.LocationStatus != "ACTIVE"))
                && l.InventoryReservations.Any(r => (r.ReservationStatus == "RESERVED" || r.ReservationStatus == "PARTIALLY_PICKED")
                && r.ReleasedAt == null && r.PickedQuantity < r.ReservedQuantity && r.Inventory.Unit == "kg"
                && r.Inventory.WarehouseLocation.LocationStatus == "ACTIVE"
                && !BatchHandlingEligibility.PendingHandlingBatches(db).Contains(r.Inventory.ProductBatchId)
                && (r.Inventory.ProductBatch.BatchStatus == "IN_STOCK" || r.Inventory.ProductBatch.BatchStatus == "RESERVED" || r.Inventory.ProductBatch.BatchStatus == "PARTIALLY_ISSUED")
                && (r.Inventory.ProductBatch.ExpiryDate == null || r.Inventory.ProductBatch.ExpiryDate >= today)))
        }).ToListAsync(ct);
        var summaries = summariesData.Select(o =>
        {
            var reason = OrderReason(o.OrderStatus, o.FullyAllocated, o.HasPickable);
            return new PickingOrderSummary(o.Id, o.OrderCode, o.DistributorName, o.ApprovedAt, o.ExpectedDeliveryDate,
                o.OrderStatus, o.Approved, o.Reserved, o.Picked, reason is null, reason);
        }).ToList();
        return new(summaries, count, query.Page, query.PageSize);
    }
    public async Task<PickingOrderDetail> GetAsync(long id, CancellationToken ct)
    { await using var db = Context(); return await ReadAsync(db, id, ct); }

    private static async Task<PickingOrderDetail> ReadAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Distributor)
            .Include(o => o.OrderDetails).ThenInclude(l => l.CropType)
            .Include(o => o.OrderDetails).ThenInclude(l => l.RequestedProductBatch)
            .Include(o => o.OrderDetails).ThenInclude(l => l.InventoryReservations).ThenInclude(r => r.Inventory).ThenInclude(i => i.ProductBatch)
            .Include(o => o.OrderDetails).ThenInclude(l => l.InventoryReservations).ThenInclude(r => r.Inventory).ThenInclude(i => i.WarehouseLocation)
            .Include(o => o.OrderDetails).ThenInclude(l => l.InventoryReservations).ThenInclude(r => r.PickingHistories).ThenInclude(h => h.PickedByAccount)
            .AsSplitQuery().SingleOrDefaultAsync(o => o.PurchaseOrderId == id, ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
        if (order.ApprovedAt is null || order.ApprovedByAccountId is null)
            throw new ConflictException("Đơn chưa được phê duyệt cho xử lý kho.");
        var blockedBatches = await db.StockAdjustments.AsNoTracking().Where(a => BatchHandlingEligibility.PendingHandlingBatches(db).Contains(a.Inventory.ProductBatchId))
            .Select(a => new { a.Inventory.ProductBatchId, a.RequestKind }).ToListAsync(ct);
        var batchIds = order.OrderDetails.Where(l => l.RequestedProductBatchId != null).Select(l => l.RequestedProductBatchId!.Value).ToList();
        var stocks = await db.Inventories.AsNoTracking().Where(i => batchIds.Contains(i.ProductBatchId)).ToListAsync(ct);
        var lines = order.OrderDetails.OrderBy(l => l.OrderDetailId).Select(l => new PickingOrderLine(l.OrderDetailId, l.CropType.CropName,
            l.RequestedProductBatchId, l.RequestedProductBatch?.BatchCode, l.RequestedProductBatch?.ProductName, l.ApprovedWeightKg,
            l.InventoryReservations.Where(r => PickingRules.AllocatedReservations.Contains(r.ReservationStatus)).Sum(r => r.ReservedQuantity),
            l.InventoryReservations.Where(r => PickingRules.AllocatedReservations.Contains(r.ReservationStatus)).Sum(r => r.PickedQuantity),
            l.InventoryReservations.OrderBy(r => r.InventoryReservationId).Select(r =>
            {
                var reason = ReservationReason(r);
                var wholeLotReason = WholeLotFulfillment.Reason(l, l.InventoryReservations.ToList(), stocks.Where(i => i.ProductBatchId == r.Inventory.ProductBatchId).ToList());
                var otherPositionReason = l.InventoryReservations.Where(p => p.ReleasedAt == null && PickingRules.ActiveReservations.Contains(p.ReservationStatus))
                    .Select(p => p.Inventory.WarehouseLocation.LocationStatus != "ACTIVE" || p.Inventory.Unit != "kg"
                        ? "Vị trí kho không hoạt động hoặc đơn vị tồn kho không phải kg." : PickingRules.BatchLockReason(p.Inventory.ProductBatch.BatchStatus, p.Inventory.ProductBatch.ExpiryDate))
                    .FirstOrDefault(value => value != null);
                reason ??= wholeLotReason ?? otherPositionReason;
                var pending = blockedBatches.Where(a => a.ProductBatchId == r.Inventory.ProductBatchId).ToList();
                if (pending.Count > 0) reason = pending.Any(a => a.RequestKind == "DISPOSAL")
                    ? SAW.Application.Features.StockAdjustments.StockAdjustmentRules.DisposalLockReason
                    : SAW.Application.Features.StockAdjustments.StockAdjustmentRules.CountLockReason;
                return new PickingReservation(r.InventoryReservationId, r.InventoryId, r.Inventory.ProductBatchId,
                    r.Inventory.ProductBatch.BatchCode, r.Inventory.ProductBatch.ProductName, r.Inventory.WarehouseLocationId,
                    r.Inventory.WarehouseLocation.LocationCode, r.Inventory.ProductBatch.ExpiryDate, r.ReservationStatus,
                    r.ReservedQuantity, r.PickedQuantity, reason is null, reason,
                    r.PickingHistories.OrderByDescending(h => h.PickedAt).ThenByDescending(h => h.PickingHistoryId)
                        .Select(h => new PickingHistoryItem(h.PickingHistoryId, h.PickedQuantity, h.PickedAt, h.PickedByAccount.FullName, h.Note)).ToList());
            }).ToList())).ToList();
        var orderHold = await BatchHandlingEligibility.OrderHeldAsync(db, id, ct);
        var lockReason = order.OrderStatus == "CANCELLED" ? "Đơn đã hủy — không tiếp tục soạn." : orderHold ? "Đơn đang tạm dừng xử lý điều chỉnh tồn; chưa được soạn." : OrderReason(order.OrderStatus, lines.Count > 0 && lines.All(l => l.ApprovedWeightKg > 0 && l.ReservedWeightKg == l.ApprovedWeightKg),
            lines.Any(l => l.Reservations.Any(r => r.CanPick)));
        var adjustments = await db.StockAdjustmentResolutions.AsNoTracking().Where(r => r.PurchaseOrderId == id)
            .OrderByDescending(r => r.StockAdjustmentId).Select(r => new PickingAdjustmentWork(r.StockAdjustmentId, r.StockAdjustment.AdjustmentCode, r.State)).ToListAsync(ct);
        return new(new(order.PurchaseOrderId, order.OrderCode, order.Distributor.DistributorName, order.ApprovedAt,
            order.ExpectedDeliveryDate, order.OrderStatus, lines.Sum(l => l.ApprovedWeightKg), lines.Sum(l => l.ReservedWeightKg),
            lines.Sum(l => l.PickedWeightKg), lockReason is null, lockReason), lines, adjustments);
    }
    private static string? OrderReason(string status, bool fullyAllocated, bool hasPickable) =>
        status == "APPROVED" ? "Đơn đã được duyệt, đang chờ phân bổ đủ hàng; chưa thể xác nhận soạn hàng."
        : status == "PICKED" ? "Đơn đã soạn đủ các lô; có thể lập phiếu xuất kho."
        : status is not ("RESERVED" or "PARTIALLY_PICKED" or "PARTIALLY_ISSUED")
            ? status == "CANCELLED" ? "Đơn đã hủy — không tiếp tục soạn." : "Đơn không còn cho phép soạn hàng ở trạng thái hiện tại."
        : !fullyAllocated
            ? "Đơn đang chờ phân bổ đủ hàng đã duyệt."
        : !hasPickable ? "Không có phần giữ hàng có thể soạn; kiểm tra lý do ở từng lô."
        : null;
    private static string? ReservationReason(InventoryReservation r) =>
        r.ReservationStatus is not ("RESERVED" or "PARTIALLY_PICKED") || r.ReleasedAt != null || r.PickedQuantity >= r.ReservedQuantity
            ? "Phần giữ hàng đã soạn đủ hoặc không còn hiệu lực."
        : r.Inventory.WarehouseLocation.LocationStatus != "ACTIVE" ? "Vị trí kho không hoạt động."
        : r.Inventory.Unit != "kg" ? "Đơn vị tồn kho phải là kg."
        : PickingRules.BatchLockReason(r.Inventory.ProductBatch.BatchStatus, r.Inventory.ProductBatch.ExpiryDate);

    public Task ConfirmAsync(long orderId, ConfirmPickingRequest request, int actorId, CancellationToken ct) =>
        WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db =>
        {
            var order = await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM PURCHASE_ORDER WITH (UPDLOCK,HOLDLOCK) WHERE PurchaseOrderID={orderId}")
                .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
            // Existing audit storage records the operation receipt in the same transaction. The order's
            // SQL update lock serializes this lookup with every other picking request for this order.
            // No new column/table or edits to immutable PICKING_HISTORY are needed for idempotency.
            var key = request.RequestId.ToString();
            var receipts = await db.AuditLogs.AsNoTracking().Where(a => a.ActionType == "CONFIRM_PICKING"
                && a.EntityName == "PURCHASE_ORDER" && a.EntityId == orderId.ToString() && a.NewDataJson != null
                && a.NewDataJson.Contains(key)).ToListAsync(ct);
            var previous = receipts.Select(a => new { Audit = a, Data = JsonSerializer.Deserialize<PickingReceipt>(a.NewDataJson!) })
                .SingleOrDefault(a => a.Data?.RequestId == request.RequestId);
            if (previous is not null)
            {
                var saved = previous.Data!;
                if (previous.Audit.AccountId != actorId || saved.ReservationId != request.ReservationId
                    || saved.OrderDetailId != request.OrderDetailId || saved.InventoryId != request.InventoryId
                    || saved.ProductBatchId != request.ProductBatchId || saved.QuantityKg != request.QuantityKg
                    || saved.ExpectedPickedQuantityKg != request.ExpectedPickedQuantityKg || saved.Note != request.Note)
                    throw new ConflictException("Mã thao tác đã được sử dụng cho dữ liệu khác. Vui lòng tải lại đơn.");
                return true; // A lost response/retry never adds another history or audit.
            }
            if (order.OrderStatus == "CANCELLED") throw new ConflictException("Đơn đã hủy — không tiếp tục soạn.");
            if (await BatchHandlingEligibility.OrderHeldAsync(db, orderId, ct)) throw new ConflictException("Đơn đang tạm dừng xử lý điều chỉnh tồn.");
            var before = await ReadAsync(db, orderId, ct);
            if (order.OrderStatus is not ("RESERVED" or "PARTIALLY_PICKED" or "PARTIALLY_ISSUED") || before.Lines.Count == 0
                || before.Lines.Any(l => l.ApprovedWeightKg <= 0 || l.ReservedWeightKg != l.ApprovedWeightKg))
                throw new ConflictException(before.Order.PickingLockReason ?? "Đơn chưa có đủ phần giữ hàng hợp lệ.");
            // Validate the selected reservation below even when no other reservation is pickable,
            // so a stale open form receives the actual expiry/location reason rather than a generic lock.
            var batch = await WarehouseOperationTransaction.LockBatchAsync(db, request.ProductBatchId, ct);
            var stocks = await WarehouseOperationTransaction.LockInventoryAsync(db, batch.ProductBatchId, ct);
            if (await BatchHandlingEligibility.HandlingReasonAsync(db, batch.ProductBatchId, ct) is { } handlingReason)
                throw new ConflictException(handlingReason);
            var reservation = await db.InventoryReservations.FromSqlInterpolated($"SELECT * FROM INVENTORY_RESERVATION WITH (UPDLOCK,HOLDLOCK) WHERE InventoryReservationID={request.ReservationId}")
                .Include(r => r.OrderDetail).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy phần giữ hàng.");
            var inventory = stocks.SingleOrDefault(i => i.InventoryId == request.InventoryId);
            if (inventory is null || reservation.OrderDetail.PurchaseOrderId != orderId || reservation.OrderDetailId != request.OrderDetailId
                || reservation.InventoryId != request.InventoryId || reservation.OrderDetail.CropTypeId != batch.CropTypeId
                || (reservation.OrderDetail.RequestedProductBatchId is { } expectedBatch && expectedBatch != batch.ProductBatchId))
                throw new ConflictException("Phần giữ hàng không thuộc đúng đơn, dòng hàng, tồn kho và lô đã chọn.");
            var held = await db.InventoryReservations.FromSqlInterpolated($"SELECT * FROM INVENTORY_RESERVATION WITH (UPDLOCK,HOLDLOCK) WHERE OrderDetailID={request.OrderDetailId}")
                .Include(r => r.Inventory).OrderBy(r => r.InventoryReservationId).ToListAsync(ct);
            var wholeLotReason = WholeLotFulfillment.Reason(reservation.OrderDetail, held, stocks);
            if (wholeLotReason is not null) throw new ConflictException(wholeLotReason);
            foreach (var position in stocks.Where(i => i.QuantityOnHand > 0).OrderBy(i => i.WarehouseLocationId))
            {
                var location = await db.WarehouseLocations.FromSqlInterpolated($"SELECT * FROM WAREHOUSE_LOCATION WITH (UPDLOCK,HOLDLOCK) WHERE WarehouseLocationID={position.WarehouseLocationId}").SingleAsync(ct);
                if (location.LocationStatus != "ACTIVE" || position.Unit != "kg")
                    throw new ConflictException("Vị trí kho không hoạt động hoặc đơn vị tồn kho không phải kg.");
            }
            var reason = PickingRules.BatchLockReason(batch.BatchStatus, batch.ExpiryDate);
            if (reason is not null) throw new ConflictException(reason);
            if (reservation.ReservationStatus is not ("RESERVED" or "PARTIALLY_PICKED") || reservation.ReleasedAt != null)
                throw new ConflictException("Phần giữ hàng không còn cho phép soạn hàng.");
            var targets = held.Where(r => r.ReleasedAt == null && PickingRules.ActiveReservations.Contains(r.ReservationStatus)).ToList();
            var oldPicked = targets.Sum(r => r.PickedQuantity);
            var remaining = targets.Sum(r => r.ReservedQuantity - r.PickedQuantity);
            if (oldPicked != request.ExpectedPickedQuantityKg)
                throw new ConflictException("Dữ liệu soạn hàng đã thay đổi. Vui lòng tải lại trước khi xác nhận.");
            if (remaining <= 0 || request.QuantityKg != remaining)
                throw new ConflictException("Chỉ xác nhận đã soạn đủ nguyên lô. Khối lượng lấy từ phần giữ hàng; vui lòng tải lại để kiểm tra.");
            var histories = targets.Where(r => r.PickedQuantity < r.ReservedQuantity).Select(r => new PickingHistory {
                InventoryReservationId = r.InventoryReservationId, PickedByAccountId = actorId,
                PickedQuantity = r.ReservedQuantity - r.PickedQuantity, PickedAt = DateTime.UtcNow, Note = request.Note }).ToList();
            db.PickingHistories.AddRange(histories);
            // SQL TR_PICKING_HISTORY_SYNC updates PickedQuantity/status; its reservation trigger updates kg counters.
            // Never update those columns or QuantityOnHand again in application code.
            await db.SaveChangesAsync(ct);
            var active = await db.InventoryReservations.AsNoTracking().Where(r => r.OrderDetail.PurchaseOrderId == orderId
                && PickingRules.ActiveReservations.Contains(r.ReservationStatus)).ToListAsync(ct);
            var updated = active.SingleOrDefault(r => r.InventoryReservationId == request.ReservationId);
            if (updated is null || targets.Any(t => !active.Any(r => r.InventoryReservationId == t.InventoryReservationId
                && r.PickedQuantity == t.ReservedQuantity && r.ReservationStatus == "PICKED")))
                throw new ConflictException("Cơ chế đồng bộ soạn hàng của kho chưa sẵn sàng. Chưa lưu thao tác; vui lòng liên hệ quản trị.");
            // Issuing a partial pick must not prevent picking the remaining held stock or move
            // an already partially issued order backwards to PICKED.
            if (order.OrderStatus != "PARTIALLY_ISSUED")
                order.OrderStatus = active.Count > 0 && active.All(r => r.PickedQuantity == r.ReservedQuantity) ? "PICKED" : "PARTIALLY_PICKED";
            order.UpdatedAt = DateTime.UtcNow;
            db.AuditLogs.Add(new AuditLog { AccountId = actorId, ActionType = "CONFIRM_PICKING", EntityName = "PURCHASE_ORDER",
                EntityId = orderId.ToString(), OldDataJson = JsonSerializer.Serialize(new { PickedQuantityKg = oldPicked,
                    reservation.ReservationStatus, OrderStatus = before.Order.Status }),
                NewDataJson = JsonSerializer.Serialize(new PickingReceipt(request.RequestId, request.ReservationId, request.OrderDetailId,
                    request.InventoryId, request.ProductBatchId, request.QuantityKg, request.ExpectedPickedQuantityKg, request.Note,
                    histories[0].PickingHistoryId, targets.Sum(r => r.ReservedQuantity), updated.ReservationStatus, order.OrderStatus,
                    histories.Select(h => h.PickingHistoryId).ToList())),
                Description = "Xác nhận đã soạn đủ nguyên lô tại tất cả vị trí; không giảm tồn kho. " + request.Note, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    private sealed record PickingReceipt(Guid RequestId, long ReservationId, long OrderDetailId, long InventoryId,
        long ProductBatchId, decimal QuantityKg, decimal ExpectedPickedQuantityKg, string? Note,
        long PickingHistoryId, decimal PickedQuantityKg, string ReservationStatus, string OrderStatus,
        IReadOnlyList<long>? PickingHistoryIds = null);
}
