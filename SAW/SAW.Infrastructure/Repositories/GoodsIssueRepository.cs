using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsIssues;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using GoodsIssueDetail = SAW.Application.Features.GoodsIssues.GoodsIssueDetail;

namespace SAW.Infrastructure.Repositories;

public sealed partial class GoodsIssueRepository(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor) : IGoodsIssueRepository
{
    private AppDbContext Context() => new(options, accessor);
    public Task<long> CreateAsync(CreateGoodsIssueRequest request, int actorId, CancellationToken ct) =>
        WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db =>
        {
            var order = await LockOrderAsync(db, request.PurchaseOrderId, ct);
            var payload = JsonSerializer.Serialize(request);
            if (await ReplayAsync(db, "CREATE_GOODS_ISSUE", request.RequestId, actorId, payload, ct) is { } previous) return previous;
            await EnsureDatabaseReadyAsync(db, ct);
            var allocations = await LockAllocationsAsync(db, order, ct);
            await ValidateLinesAsync(db, order, request.Lines, allocations, ct);
            var issue = new GoodsIssue { IssueCode = "GI-" + Guid.NewGuid().ToString("N").ToUpperInvariant(),
                PurchaseOrderId = order.PurchaseOrderId, OperationAccountId = actorId, IssueStatus = "DRAFT",
                IssuedAt = request.IssuedDate.ToDateTime(TimeOnly.MinValue), ReceiverName = request.ReceiverName, Note = request.Note };
            db.GoodsIssues.Add(issue);
            foreach (var line in request.Lines) issue.GoodsIssueDetails.Add(Line(line, allocations));
            await db.SaveChangesAsync(ct);
            Receipt(db, "CREATE_GOODS_ISSUE", request.RequestId, actorId, payload, issue.GoodsIssueId, null,
                new { issue.IssueCode, issue.IssuedAt, issue.ReceiverName, issue.Note, Lines = request.Lines }, "Lưu phiếu xuất nháp; chưa giảm tồn kho.");
            await db.SaveChangesAsync(ct); return issue.GoodsIssueId;
        }, ct);

    public async Task UpdateDraftAsync(long id, UpdateGoodsIssueDraftRequest request, int actorId, CancellationToken ct)
    {
        var orderId = await FindOrderAsync(id, ct);
        await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db =>
        {
            var order = await LockOrderAsync(db, orderId, ct);
            var payload = JsonSerializer.Serialize(new { Id = id, Request = request });
            if (await ReplayAsync(db, "UPDATE_GOODS_ISSUE", request.RequestId, actorId, payload, ct) is not null) return true;
            var issue = await LockIssueAsync(db, id, ct);
            var before = await ReadAsync(db, id, ct);
            EnsureDraft(issue, before, request.ExpectedSnapshot);
            await EnsureDatabaseReadyAsync(db, ct);
            var allocations = await LockAllocationsAsync(db, order, ct);
            await ValidateLinesAsync(db, order, request.Lines, allocations, ct);
            // Fixed order, code and creator. Quantity/unit are always kg, derived from selected reservations.
            db.GoodsIssueDetails.RemoveRange(issue.GoodsIssueDetails);
            await db.SaveChangesAsync(ct);
            issue.GoodsIssueDetails.Clear();
            foreach (var line in request.Lines) issue.GoodsIssueDetails.Add(Line(line, allocations));
            issue.IssuedAt = request.IssuedDate.ToDateTime(TimeOnly.MinValue); issue.ReceiverName = request.ReceiverName; issue.Note = request.Note;
            Receipt(db, "UPDATE_GOODS_ISSUE", request.RequestId, actorId, payload, id, before,
                new { issue.IssuedAt, issue.ReceiverName, issue.Note, Lines = request.Lines }, "Chỉnh sửa phiếu xuất nháp; chưa giảm tồn kho.");
            await db.SaveChangesAsync(ct); return true;
        }, ct);
    }
    public async Task ConfirmAsync(long id, ConfirmGoodsIssueRequest request, int actorId, CancellationToken ct)
    {
        var orderId = await FindOrderAsync(id, ct);
        await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db =>
        {
            var order = await LockOrderAsync(db, orderId, ct);
            var payload = JsonSerializer.Serialize(new { Id = id, Request = request });
            if (await ReplayAsync(db, "CONFIRM_GOODS_ISSUE", request.RequestId, actorId, payload, ct) is not null) return true;
            var issue = await LockIssueAsync(db, id, ct);
            var before = await ReadAsync(db, id, ct); EnsureDraft(issue, before, request.ExpectedSnapshot);
            GoodsIssueRules.Draft(DateOnly.FromDateTime(issue.IssuedAt), issue.ReceiverName, issue.Note,
                issue.GoodsIssueDetails.Select(l => new GoodsIssueLineRequest(l.InventoryReservationId, l.WeightInKg)).ToList());
            await EnsureDatabaseReadyAsync(db, ct);
            var allocations = await LockAllocationsAsync(db, order, ct);
            var lines = issue.GoodsIssueDetails.Select(l => new GoodsIssueLineRequest(l.InventoryReservationId, l.WeightInKg)).ToList();
            foreach (var line in issue.GoodsIssueDetails)
            {
                if (!allocations.TryGetValue(line.InventoryReservationId, out var r) || line.InventoryId != r.InventoryId
                    || line.OrderDetailId != r.OrderDetailId || line.Unit != "kg" || line.IssuedQuantity != line.WeightInKg)
                    throw new ConflictException("Chi tiết phiếu không khớp phần giữ hàng và đơn vị kg.");
            }
            await ValidateLinesAsync(db, order, lines, allocations, ct);
            var beforeState = new { Document = before, order.OrderStatus,
                Inventory = allocations.Values.Select(r => r.Inventory).DistinctBy(i => i.InventoryId).OrderBy(i => i.InventoryId)
                    .Select(i => new { i.InventoryId, i.QuantityOnHand, i.ReservedQuantity }).ToList(),
                Reservations = allocations.Values.OrderBy(r => r.InventoryReservationId)
                    .Select(r => new { r.InventoryReservationId, r.ReservedQuantity, r.PickedQuantity, r.ReservationStatus }).ToList() };
            var now = DateTime.UtcNow;
            issue.IssueStatus = "COMMITTED"; issue.CommittedAt = now;
            await db.SaveChangesAsync(ct);
            // The reservation trigger installed by FixGoodsIssueWorkflowTriggers owns net held kg. Preserve
            // original reserved/picked quantities and immutable picking history, including partial issues.
            foreach (var line in lines.OrderBy(l => l.ReservationId))
            {
                var r = allocations[line.ReservationId];
                var issued = await IssuedAsync(db, r.InventoryReservationId, ct);
                var status = issued == r.ReservedQuantity ? "ISSUED" : r.PickedQuantity == r.ReservedQuantity ? "PICKED" : "PARTIALLY_PICKED";
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE INVENTORY_RESERVATION SET ReservationStatus={status} WHERE InventoryReservationID={r.InventoryReservationId}", ct);
            }
            // No GOODS_ISSUE trigger subtracts stock. Application changes QOH once, after net held
            // counters are synchronized; computed AvailableQuantity is never assigned.
            foreach (var group in lines.GroupBy(l => allocations[l.ReservationId].InventoryId).OrderBy(g => g.Key))
            {
                var stock = allocations[group.First().ReservationId].Inventory;
                stock.QuantityOnHand -= group.Sum(l => l.WeightInKg); stock.LastUpdatedAt = now;
            }
            var orderLines = await db.OrderDetails.AsNoTracking().Where(l => l.PurchaseOrderId == orderId)
                .Select(l => new { l.ApprovedWeightKg, Issued = l.GoodsIssueDetails.Where(d => d.GoodsIssue.IssueStatus == "COMMITTED").Sum(d => (decimal?)d.WeightInKg) ?? 0 }).ToListAsync(ct);
            order.OrderStatus = orderLines.Count > 0 && orderLines.All(l => l.Issued == l.ApprovedWeightKg) ? "ISSUED" : "PARTIALLY_ISSUED";
            order.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            foreach (var batchId in lines.Select(l => allocations[l.ReservationId].Inventory.ProductBatchId).Distinct().Order())
            {
                var batch = db.ProductBatches.Local.Single(b => b.ProductBatchId == batchId);
                batch.BatchStatus = await db.Inventories.Where(i => i.ProductBatchId == batchId).SumAsync(i => i.QuantityOnHand, ct) == 0 ? "ISSUED" : "PARTIALLY_ISSUED";
                batch.UpdatedAt = now;
            }
            var inventoryIds = allocations.Values.Select(r => r.InventoryId).Distinct().ToArray();
            var afterStock = await db.Inventories.AsNoTracking().Where(i => inventoryIds.Contains(i.InventoryId)).OrderBy(i => i.InventoryId)
                .Select(i => new { i.InventoryId, i.QuantityOnHand, i.ReservedQuantity }).ToListAsync(ct);
            var afterReservations = await db.InventoryReservations.AsNoTracking().Where(r => r.OrderDetail.PurchaseOrderId == orderId)
                .OrderBy(r => r.InventoryReservationId).Select(r => new { r.InventoryReservationId, r.ReservedQuantity, r.PickedQuantity, r.ReservationStatus }).ToListAsync(ct);
            Receipt(db, "CONFIRM_GOODS_ISSUE", request.RequestId, actorId, payload, id, beforeState,
                new { issue.IssueStatus, issue.CommittedAt, order.OrderStatus, Lines = lines, Inventory = afterStock, Reservations = afterReservations },
                "Xác nhận xuất kho theo kg; tiêu thụ đúng phần giữ hàng đã xuất.");
            await db.SaveChangesAsync(ct); return true;
        }, ct);
    }
    private async Task<long> FindOrderAsync(long id, CancellationToken ct)
    {
        await using var db = Context();
        return await db.GoodsIssues.Where(g => g.GoodsIssueId == id).Select(g => (long?)g.PurchaseOrderId).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Không tìm thấy phiếu xuất kho.");
    }
    private static async Task<PurchaseOrder> LockOrderAsync(AppDbContext db, long id, CancellationToken ct) =>
        await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM PURCHASE_ORDER WITH (UPDLOCK,HOLDLOCK) WHERE PurchaseOrderID={id}").SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
    private static async Task<GoodsIssue> LockIssueAsync(AppDbContext db, long id, CancellationToken ct) =>
        await db.GoodsIssues.FromSqlInterpolated($"SELECT * FROM GOODS_ISSUE WITH (UPDLOCK,HOLDLOCK) WHERE GoodsIssueID={id}")
            .Include(g => g.GoodsIssueDetails).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy phiếu xuất kho.");
    private static void EnsureDraft(GoodsIssue issue, GoodsIssueDetail before, string snapshot)
    {
        if (!before.CanEdit) throw new ConflictException(before.LockReason ?? "Phiếu không còn được sửa hoặc xác nhận.");
        if (issue.IssueStatus != "DRAFT") throw new ConflictException("Phiếu đã xác nhận xuất kho; không thể sửa hoặc xác nhận phiên bản khác.");
        if (before.Snapshot != snapshot) throw new ConflictException("Phiếu đã được người khác thay đổi. Vui lòng tải lại và kiểm tra trước khi lưu hoặc xác nhận.");
    }
    private static async Task<Dictionary<long, InventoryReservation>> LockAllocationsAsync(AppDbContext db, PurchaseOrder order, CancellationToken ct)
    {
        if (await BatchHandlingEligibility.OrderHeldAsync(db, order.PurchaseOrderId, ct)) throw new ConflictException("Đơn đang tạm dừng xử lý điều chỉnh tồn.");
        if (!GoodsIssueRules.OrderStatuses.Contains(order.OrderStatus) || order.ApprovedAt is null || order.ApprovedByAccountId is null)
            throw new ConflictException("Đơn chưa có hàng đã soạn hoặc không còn cho phép xuất kho.");
        var batches = await db.InventoryReservations.AsNoTracking().Where(r => r.OrderDetail.PurchaseOrderId == order.PurchaseOrderId)
            .Select(r => r.Inventory.ProductBatchId).Distinct().OrderBy(id => id).ToListAsync(ct);
        foreach (var id in batches) { await WarehouseOperationTransaction.LockBatchAsync(db, id, ct); await WarehouseOperationTransaction.LockInventoryAsync(db, id, ct); }
        var reservations = await db.InventoryReservations.FromSqlInterpolated($"SELECT r.* FROM INVENTORY_RESERVATION r WITH (UPDLOCK,HOLDLOCK) JOIN ORDER_DETAIL l ON l.OrderDetailID=r.OrderDetailID WHERE l.PurchaseOrderID={order.PurchaseOrderId}")
            .Include(r => r.OrderDetail).Include(r => r.Inventory).ThenInclude(i => i.ProductBatch).OrderBy(r => r.InventoryReservationId).ToListAsync(ct);
        foreach (var location in reservations.Select(r => r.Inventory.WarehouseLocationId).Distinct().Order())
            await db.WarehouseLocations.FromSqlInterpolated($"SELECT * FROM WAREHOUSE_LOCATION WITH (UPDLOCK,HOLDLOCK) WHERE WarehouseLocationID={location}").SingleAsync(ct);
        return reservations.ToDictionary(r => r.InventoryReservationId);
    }
    private static async Task ValidateLinesAsync(AppDbContext db, PurchaseOrder order, IReadOnlyList<GoodsIssueLineRequest> lines,
        Dictionary<long, InventoryReservation> allocations, CancellationToken ct)
    {
        GoodsIssueRules.Draft(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)), null, null, lines);
        foreach (var line in lines)
        {
            if (!allocations.TryGetValue(line.ReservationId, out var r) || r.OrderDetail.PurchaseOrderId != order.PurchaseOrderId)
                throw new ConflictException("Phần giữ hàng không thuộc đơn đã chọn.");
            if (r.OrderDetail.CropTypeId != r.Inventory.ProductBatch.CropTypeId
                || (r.OrderDetail.RequestedProductBatchId is { } selected && selected != r.Inventory.ProductBatchId))
                throw new ConflictException("Phần giữ hàng không khớp lô và dòng đơn.");
            if (r.ReservationStatus != "PICKED" || r.PickedQuantity != r.ReservedQuantity || r.ReleasedAt != null)
                throw new ConflictException("Cần xác nhận đã soạn đủ nguyên lô trước khi xuất kho.");
            var reason = PickingRules.BatchLockReason(r.Inventory.ProductBatch.BatchStatus, r.Inventory.ProductBatch.ExpiryDate);
            reason ??= await BatchHandlingEligibility.HandlingReasonAsync(db, r.Inventory.ProductBatchId, ct);
            if (reason is not null) throw new ConflictException(reason);
            if (r.Inventory.Unit != "kg" || db.WarehouseLocations.Local.Single(l => l.WarehouseLocationId == r.Inventory.WarehouseLocationId).LocationStatus != "ACTIVE")
                throw new ConflictException("Vị trí kho không hoạt động hoặc đơn vị tồn không phải kg.");
            if (line.WeightInKg != r.ReservedQuantity || await IssuedAsync(db, r.InventoryReservationId, ct) != 0)
                throw new ConflictException("Chỉ được xuất đủ nguyên lô đã soạn. Khối lượng lấy từ phần giữ hàng; lô đã xuất một phần cần được rà soát riêng.");
        }
        foreach (var group in lines.GroupBy(l => allocations[l.ReservationId].OrderDetailId))
        {
            var approved = allocations[group.First().ReservationId].OrderDetail.ApprovedWeightKg;
            var line = allocations[group.First().ReservationId].OrderDetail;
            var held = allocations.Values.Where(r => r.OrderDetailId == group.Key).ToList();
            var stocks = db.Inventories.Local.Where(i => i.ProductBatchId == line.RequestedProductBatchId).ToList();
            if (WholeLotFulfillment.Reason(line, held, stocks, requirePicked: true) is { } reason)
                throw new ConflictException(reason);
            var ids = held.Where(r => r.ReleasedAt == null && PickingRules.ActiveReservations.Contains(r.ReservationStatus))
                .Select(r => r.InventoryReservationId).Order().ToArray();
            if (!group.Select(l => l.ReservationId).Order().SequenceEqual(ids))
                throw new ConflictException("Phiếu xuất phải bao gồm đủ tất cả vị trí giữ hàng của lô đã chọn.");
            var issued = await db.GoodsIssueDetails.Where(d => d.OrderDetailId == group.Key && d.GoodsIssue.IssueStatus == "COMMITTED")
                .SumAsync(d => (decimal?)d.WeightInKg, ct) ?? 0;
            if (issued != 0 || group.Sum(l => l.WeightInKg) != approved) throw new ConflictException("Khối lượng xuất phải đúng toàn bộ lô được duyệt; không xuất từng phần của lô.");
        }
        foreach (var group in lines.GroupBy(l => allocations[l.ReservationId].InventoryId))
            if (group.Sum(l => l.WeightInKg) > allocations[group.First().ReservationId].Inventory.QuantityOnHand)
                throw new ConflictException("Khối lượng xuất vượt tồn kho thực tế.");
    }
    private static SAW.Domain.Entities.GoodsIssueDetail Line(GoodsIssueLineRequest line, Dictionary<long, InventoryReservation> allocations) =>
        new() { InventoryReservationId = line.ReservationId, OrderDetailId = allocations[line.ReservationId].OrderDetailId,
            InventoryId = allocations[line.ReservationId].InventoryId, IssuedQuantity = allocations[line.ReservationId].ReservedQuantity,
            Unit = "kg", WeightInKg = allocations[line.ReservationId].ReservedQuantity };
    private static async Task<decimal> IssuedAsync(AppDbContext db, long reservationId, CancellationToken ct) =>
        await db.GoodsIssueDetails.Where(d => d.InventoryReservationId == reservationId && d.GoodsIssue.IssueStatus == "COMMITTED").SumAsync(d => (decimal?)d.WeightInKg, ct) ?? 0;
    private sealed record OperationReceipt(Guid RequestId, string Payload, long IssueId, object? Values);
    private static async Task<long?> ReplayAsync(AppDbContext db, string action, Guid key, int actorId, string payload, CancellationToken ct)
    {
        var text = key.ToString();
        var receipts = await db.AuditLogs.AsNoTracking().Where(a => a.ActionType == action && a.EntityName == "GOODS_ISSUE"
            && a.NewDataJson != null && a.NewDataJson.Contains(text)).ToListAsync(ct);
        foreach (var audit in receipts)
        {
            var receipt = JsonSerializer.Deserialize<OperationReceipt>(audit.NewDataJson!);
            if (receipt?.RequestId != key) continue;
            if (audit.AccountId != actorId || receipt.Payload != payload) throw new ConflictException("Mã thao tác đã được dùng cho dữ liệu khác. Vui lòng tải lại.");
            return receipt.IssueId;
        }
        return null;
    }
    private static void Receipt(AppDbContext db, string action, Guid key, int actorId, string payload, long id, object? before, object? after, string description) =>
        db.AuditLogs.Add(new AuditLog { AccountId = actorId, ActionType = action, EntityName = "GOODS_ISSUE", EntityId = id.ToString(),
            OldDataJson = before is null ? null : JsonSerializer.Serialize(before), NewDataJson = JsonSerializer.Serialize(new OperationReceipt(key, payload, id, after)),
            Description = description, CreatedAt = DateTime.UtcNow });
    private static async Task EnsureDatabaseReadyAsync(AppDbContext db, CancellationToken ct)
    {
        var ready = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.triggers WHERE is_disabled=0 AND name IN ('TR_INVENTORY_RESERVATION_SYNC','TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK') AND OBJECT_DEFINITION(object_id) LIKE '%SAW_GOODS_ISSUE_V1%'").SingleAsync(ct);
        if (ready != 2) throw new ConflictException("Chức năng xuất kho chưa sẵn sàng. Vui lòng liên hệ quản trị để cập nhật cơ chế đồng bộ xuất kho.");
    }
}
