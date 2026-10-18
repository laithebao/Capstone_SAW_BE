using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsIssues;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using GoodsIssueDetail = SAW.Application.Features.GoodsIssues.GoodsIssueDetail;

namespace SAW.Infrastructure.Repositories;

public sealed partial class GoodsIssueRepository
{
    // Read paths use stored documents and never allocate, pick or change inventory.
    public async Task<GoodsIssuePage<GoodsIssueSummary>> SearchAsync(GoodsIssueQuery query, CancellationToken ct)
    {
        await using var db = Context();
        var rows = db.GoodsIssues.AsNoTracking().Where(g => g.IssueStatus == "DRAFT" || g.IssueStatus == "COMMITTED");
        if (query.Search is { } term) rows = rows.Where(g => g.IssueCode.Contains(term) || g.PurchaseOrder.OrderCode.Contains(term)
            || g.GoodsIssueDetails.Any(l => l.Inventory.ProductBatch.BatchCode.Contains(term)));
        if (query.Status is { } status) rows = rows.Where(g => status == "CANCELLED" ? db.GoodsIssueInvalidations.Any(v => v.GoodsIssueId == g.GoodsIssueId) : g.IssueStatus == status && !db.GoodsIssueInvalidations.Any(v => v.GoodsIssueId == g.GoodsIssueId));
        if (query.WarehouseLocationId is { } location) rows = rows.Where(g => g.GoodsIssueDetails.Any(l => l.Inventory.WarehouseLocationId == location));
        if (query.FromDate is { } from) { var date = from.ToDateTime(TimeOnly.MinValue); rows = rows.Where(g => g.IssuedAt >= date); }
        if (query.ToDate is { } to) { var date = to.AddDays(1).ToDateTime(TimeOnly.MinValue); rows = rows.Where(g => g.IssuedAt < date); }
        var count = await rows.CountAsync(ct);
        var ordered = query.SortBy switch
        {
            "issuedAtAsc" => rows.OrderBy(g => g.IssuedAt).ThenBy(g => g.GoodsIssueId),
            "idDesc" => rows.OrderByDescending(g => g.GoodsIssueId),
            "idAsc" => rows.OrderBy(g => g.GoodsIssueId),
            _ => rows.OrderByDescending(g => g.IssuedAt).ThenByDescending(g => g.GoodsIssueId)
        };
        var data = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(g => new
        {
            g.GoodsIssueId, g.IssueCode, g.PurchaseOrderId, g.PurchaseOrder.OrderCode,
            g.PurchaseOrder.Distributor.DistributorName, Weight = g.GoodsIssueDetails.Sum(l => (decimal?)l.WeightInKg) ?? 0,
            g.IssuedAt, Staff = g.OperationAccount.FullName, IssueStatus = db.GoodsIssueInvalidations.Any(v => v.GoodsIssueId == g.GoodsIssueId) ? "CANCELLED" : g.IssueStatus
        }).ToListAsync(ct);
        return new(data.Select(g => new GoodsIssueSummary(g.GoodsIssueId, g.IssueCode, g.PurchaseOrderId, g.OrderCode,
            g.DistributorName, g.Weight, DateOnly.FromDateTime(g.IssuedAt), g.Staff, g.IssueStatus)).ToList(), count, query.Page, query.PageSize);
    }
    public async Task<IReadOnlyList<GoodsIssueLocation>> LocationsAsync(CancellationToken ct)
    {
        await using var db = Context();
        return await db.WarehouseLocations.AsNoTracking().OrderBy(l => l.LocationCode)
            .Select(l => new GoodsIssueLocation(l.WarehouseLocationId, l.LocationCode)).ToListAsync(ct);
    }
    public async Task<GoodsIssuePage<GoodsIssueOrder>> EligibleAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        await using var db = Context();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var rows = db.PurchaseOrders.AsNoTracking().Where(o => GoodsIssueRules.OrderStatuses.Contains(o.OrderStatus)
            && o.ApprovedAt != null && o.ApprovedByAccountId != null && !BatchHandlingEligibility.HeldOrders(db).Contains(o.PurchaseOrderId)
            && o.OrderDetails.Any(l => l.RequestedProductBatchId != null
                && l.RequestedProductBatch!.Inventories.Sum(i => (decimal?)i.QuantityOnHand) == l.ApprovedWeightKg
                && !l.InventoryReservations.Any(r => r.ReleasedAt == null && PickingRules.ActiveReservations.Contains(r.ReservationStatus)
                    && (r.ReservationStatus != "PICKED" || r.PickedQuantity != r.ReservedQuantity))
                && l.InventoryReservations.Where(r => r.ReleasedAt == null && r.ReservationStatus == "PICKED").Sum(r => (decimal?)r.ReservedQuantity) == l.ApprovedWeightKg
                && l.InventoryReservations.Any(r => r.ReservationStatus == "PICKED"
                && r.ReleasedAt == null && r.Inventory.Unit == "kg" && r.Inventory.WarehouseLocation.LocationStatus == "ACTIVE"
                && !BatchHandlingEligibility.PendingHandlingBatches(db).Contains(r.Inventory.ProductBatchId)
                && (r.Inventory.ProductBatch.BatchStatus == "IN_STOCK" || r.Inventory.ProductBatch.BatchStatus == "RESERVED" || r.Inventory.ProductBatch.BatchStatus == "PARTIALLY_ISSUED")
                && (r.Inventory.ProductBatch.ExpiryDate == null || r.Inventory.ProductBatch.ExpiryDate >= today)
                && r.PickedQuantity > (r.GoodsIssueDetails.Where(d => d.GoodsIssue.IssueStatus == "COMMITTED").Sum(d => (decimal?)d.WeightInKg) ?? 0)
                && r.Inventory.QuantityOnHand > 0)));
        if (search is { } term) rows = rows.Where(o => o.OrderCode.Contains(term) || o.Distributor.DistributorName.Contains(term));
        var count = await rows.CountAsync(ct);
        var ids = await rows.OrderByDescending(o => o.ApprovedAt).ThenByDescending(o => o.PurchaseOrderId)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(o => o.PurchaseOrderId).ToListAsync(ct);
        var data = new List<GoodsIssueOrder>();
        foreach (var id in ids) data.Add(await ReadOrderAsync(db, id, ct));
        return new(data, count, page, pageSize);
    }
    public async Task<GoodsIssueOrder> OrderAsync(long id, CancellationToken ct)
    { await using var db = Context(); return await ReadOrderAsync(db, id, ct); }

    private static async Task<GoodsIssueOrder> ReadOrderAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Distributor)
            .Include(o => o.OrderDetails).ThenInclude(l => l.InventoryReservations).ThenInclude(r => r.Inventory).ThenInclude(i => i.ProductBatch)
            .Include(o => o.OrderDetails).ThenInclude(l => l.InventoryReservations).ThenInclude(r => r.Inventory).ThenInclude(i => i.WarehouseLocation)
            .Include(o => o.OrderDetails).ThenInclude(l => l.InventoryReservations).ThenInclude(r => r.GoodsIssueDetails).ThenInclude(d => d.GoodsIssue)
            .AsSplitQuery().SingleOrDefaultAsync(o => o.PurchaseOrderId == id, ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
        var orderHeld = await BatchHandlingEligibility.OrderHeldAsync(db, id, ct);
        var blockedBatches = await db.StockAdjustments.AsNoTracking().Where(a => BatchHandlingEligibility.PendingHandlingBatches(db).Contains(a.Inventory.ProductBatchId))
            .Select(a => new { a.Inventory.ProductBatchId, a.RequestKind }).ToListAsync(ct);
        var batchIds = order.OrderDetails.Where(l => l.RequestedProductBatchId != null).Select(l => l.RequestedProductBatchId!.Value).ToList();
        var stocks = await db.Inventories.AsNoTracking().Where(i => batchIds.Contains(i.ProductBatchId)).ToListAsync(ct);
        var allocations = order.OrderDetails.OrderBy(l => l.OrderDetailId).SelectMany(l => l.InventoryReservations
            .Where(r => (r.ReleasedAt == null && PickingRules.ActiveReservations.Contains(r.ReservationStatus))
                || r.GoodsIssueDetails.Any(d => d.GoodsIssue.IssueStatus == "COMMITTED"))
            .OrderBy(r => r.InventoryReservationId).Select(r =>
        {
            var issued = r.GoodsIssueDetails.Where(d => d.GoodsIssue.IssueStatus == "COMMITTED").Sum(d => d.WeightInKg);
            var remaining = Math.Max(0, r.PickedQuantity - issued);
            var reason = orderHeld ? "Đơn đang tạm dừng xử lý điều chỉnh tồn." : !GoodsIssueRules.OrderStatuses.Contains(order.OrderStatus) || order.ApprovedAt is null || order.ApprovedByAccountId is null
                ? "Đơn không còn cho phép xuất kho."
                : r.ReservationStatus != "PICKED" || r.ReleasedAt != null || remaining <= 0
                    ? "Phần giữ hàng chưa được soạn hoặc đã xuất hết."
                : r.Inventory.Unit != "kg" || r.Inventory.WarehouseLocation.LocationStatus != "ACTIVE"
                    ? "Vị trí không hoạt động hoặc đơn vị tồn không phải kg."
                : PickingRules.BatchLockReason(r.Inventory.ProductBatch.BatchStatus, r.Inventory.ProductBatch.ExpiryDate);
            if (reason is null && (r.Inventory.QuantityOnHand <= 0 || l.ApprovedWeightKg <= 0)) reason = "Tồn thực tế hoặc lượng duyệt không đủ.";
            reason ??= WholeLotFulfillment.Reason(l, l.InventoryReservations.ToList(), stocks.Where(i => i.ProductBatchId == r.Inventory.ProductBatchId).ToList(), requirePicked: true);
            if (reason is null && l.InventoryReservations.Any(p => p.GoodsIssueDetails.Any(d => d.GoodsIssue.IssueStatus == "COMMITTED")))
                reason = "Lô đã xuất một phần hoặc xuất hết; cần rà soát hồ sơ, không tiếp tục xuất từng phần.";
            if (reason is null && l.InventoryReservations.Where(p => p.ReleasedAt == null && PickingRules.ActiveReservations.Contains(p.ReservationStatus))
                .Any(p => p.Inventory.Unit != "kg" || p.Inventory.WarehouseLocation.LocationStatus != "ACTIVE"))
                reason = "Có vị trí giữ hàng không hoạt động hoặc đơn vị tồn không phải kg.";
            var pending = blockedBatches.Where(a => a.ProductBatchId == r.Inventory.ProductBatchId).ToList();
            if (pending.Count > 0) reason = pending.Any(a => a.RequestKind == "DISPOSAL")
                ? SAW.Application.Features.StockAdjustments.StockAdjustmentRules.DisposalLockReason
                : SAW.Application.Features.StockAdjustments.StockAdjustmentRules.CountLockReason;
            return new GoodsIssueAllocation(r.InventoryReservationId, l.OrderDetailId, r.InventoryId, r.Inventory.ProductBatchId,
                r.Inventory.ProductBatch.BatchCode, r.Inventory.ProductBatch.ProductName, r.Inventory.WarehouseLocationId,
                r.Inventory.WarehouseLocation.LocationCode, r.Inventory.ProductBatch.ExpiryDate, l.ApprovedWeightKg,
                r.ReservedQuantity, r.PickedQuantity, issued, remaining, r.Inventory.QuantityOnHand, reason is null, reason);
        })).ToList();
        return new(order.PurchaseOrderId, order.OrderCode, order.Distributor.DistributorName, order.OrderStatus, allocations);
    }
    public async Task<GoodsIssueDetail> GetAsync(long id, CancellationToken ct)
    { await using var db = Context(); return await ReadAsync(db, id, ct); }

    private static async Task<GoodsIssueDetail> ReadAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var g = await db.GoodsIssues.AsNoTracking().Include(g => g.OperationAccount).Include(g => g.PurchaseOrder).ThenInclude(o => o.Distributor)
            .Include(g => g.GoodsIssueDetails).ThenInclude(d => d.Inventory).ThenInclude(i => i.ProductBatch)
            .Include(g => g.GoodsIssueDetails).ThenInclude(d => d.Inventory).ThenInclude(i => i.WarehouseLocation)
            .AsSplitQuery().SingleOrDefaultAsync(g => g.GoodsIssueId == id, ct) ?? throw new NotFoundException("Không tìm thấy phiếu xuất kho.");
        var lines = g.GoodsIssueDetails.OrderBy(l => l.GoodsIssueDetailId).Select(l => new GoodsIssueLine(l.GoodsIssueDetailId,
            l.InventoryReservationId, l.OrderDetailId, l.InventoryId, l.Inventory.ProductBatchId, l.Inventory.ProductBatch.BatchCode,
            l.Inventory.ProductBatch.ProductName, l.Inventory.WarehouseLocationId, l.Inventory.WarehouseLocation.LocationCode,
            l.IssuedQuantity, l.Unit, l.WeightInKg)).ToList();
        var history = await db.AuditLogs.AsNoTracking().Where(a => a.EntityName == "GOODS_ISSUE" && a.EntityId == id.ToString()
            && (a.ActionType == "CREATE_GOODS_ISSUE" || a.ActionType == "UPDATE_GOODS_ISSUE" || a.ActionType == "CONFIRM_GOODS_ISSUE"))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.AuditLogId)
            .Select(a => new GoodsIssueHistory(a.AuditLogId, a.ActionType, a.CreatedAt, a.Account == null ? null : a.Account.FullName, a.Description)).ToListAsync(ct);
        // Include the immutable operation receipt ID: an edit/revert cannot recreate an old version (ABA).
        var invalid = await db.GoodsIssueInvalidations.AsNoTracking().SingleOrDefaultAsync(v => v.GoodsIssueId == id, ct);
        var orderHold = await BatchHandlingEligibility.OrderHeldAsync(db, g.PurchaseOrderId, ct);
        var effectiveStatus = invalid != null ? "CANCELLED" : g.IssueStatus;
        var canEdit = g.IssueStatus == "DRAFT" && invalid == null && !orderHold && GoodsIssueRules.OrderStatuses.Contains(g.PurchaseOrder.OrderStatus);
        var editReason = invalid?.Reason ?? (orderHold ? "Đơn đang tạm dừng xử lý điều chỉnh tồn." : "Phiếu đã xác nhận; chỉ được xem.");
        var snapshot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { g.GoodsIssueId, g.PurchaseOrderId, g.IssuedAt, g.ReceiverName, g.Note, g.IssueStatus, g.CommittedAt, Lines = lines, Version = history.FirstOrDefault()?.Id }))));
        return new(new(g.GoodsIssueId, g.IssueCode, g.PurchaseOrderId, g.PurchaseOrder.OrderCode, g.PurchaseOrder.Distributor.DistributorName,
            lines.Sum(l => l.WeightInKg), DateOnly.FromDateTime(g.IssuedAt), g.OperationAccount.FullName, effectiveStatus),
            g.ReceiverName, g.Note, g.CommittedAt, snapshot, canEdit,
            canEdit ? null : editReason, lines, history);
    }
}
