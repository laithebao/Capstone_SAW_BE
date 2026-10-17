using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts;
using SAW.Application.Features.Picking;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    public async Task<AdjustmentReviewDetail> GetReviewAsync(long id, CancellationToken ct)
    {
        await using var db = Context();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var state = await ReadReviewAsync(db, id, ct); await tx.CommitAsync(ct); return state;
        });
    }

    private static async Task<AdjustmentReviewDetail> ReadReviewAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var a = await Details(db).SingleOrDefaultAsync(a => a.StockAdjustmentId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy phiếu điều chỉnh.");
        var batch = a.Inventory.ProductBatch;
        var all = await db.Inventories.AsNoTracking().Include(i => i.WarehouseLocation)
            .Where(i => i.ProductBatchId == batch.ProductBatchId).OrderBy(i => i.InventoryId).ToListAsync(ct);
        var scope = a.RequestKind == "DISPOSAL" ? all : all.Where(i => i.InventoryId == a.InventoryId).ToList();
        var ids = scope.Select(i => i.InventoryId).ToArray();
        var reservations = await db.InventoryReservations.AsNoTracking().Where(r => ids.Contains(r.InventoryId))
            .OrderBy(r => r.InventoryReservationId).Select(r => new AdjustmentReservation(r.InventoryReservationId,
                r.OrderDetail.PurchaseOrder.OrderCode, r.ReservationStatus, r.ReservedQuantity, r.PickedQuantity)).ToListAsync(ct);
        var currentJson = await SnapshotJsonAsync(db, batch, scope, ct);
        var hasSnapshot = a.InventorySnapshotJson != null;
        var scopeMatches = a.RequestKind == "COUNT"
            ? scope.Count == 1 && ((a.Lines.Count == 1 && a.Lines.Single().InventoryId == a.InventoryId
                && a.Lines.Single().SystemQuantity == a.SystemQuantity && a.Lines.Single().ActualQuantity == a.ActualQuantity)
                || (!hasSnapshot && a.RequestId == null && a.Lines.Count == 0))
            : a.RequestKind == "DISPOSAL" && a.Lines.Count == all.Count && a.Lines.Select(l => l.InventoryId).Order().SequenceEqual(all.Select(i => i.InventoryId));
        var evidenceMatches = scopeMatches && a.SystemQuantity == scope.Sum(i => i.QuantityOnHand)
            && (hasSnapshot ? Hash(a.InventorySnapshotJson!) == Hash(currentJson) : a.RequestKind == "COUNT");
        var draftIssues = await db.GoodsIssueDetails.AsNoTracking().Where(d => ids.Contains(d.InventoryId) && d.GoodsIssue.IssueStatus == "DRAFT")
            .Select(d => d.GoodsIssueId).Distinct().OrderBy(v => v).ToListAsync(ct);
        var openTransfers = await db.StockTransfers.AsNoTracking().Where(t => t.ProductBatchId == batch.ProductBatchId
                && t.TransferStatus != "COMPLETED" && t.TransferStatus != "CANCELLED" && t.TransferStatus != "REJECTED")
            .OrderBy(t => t.StockTransferId).Select(t => t.StockTransferId).ToListAsync(ct);
        var draftReceipts = await db.GoodsReceipts.AsNoTracking().Where(r => r.ProductBatchId == batch.ProductBatchId && r.ReceiptStatus == "DRAFT")
            .OrderBy(r => r.GoodsReceiptId).Select(r => r.GoodsReceiptId).ToListAsync(ct);
        var active = await db.InventoryReservations.AsNoTracking().AnyAsync(r => ids.Contains(r.InventoryId)
            && PickingRules.ActiveReservations.Contains(r.ReservationStatus), ct);
        var reason = a.AdjustmentStatus != "PENDING" ? "Yêu cầu đã được xử lý; chỉ được xem."
            : string.IsNullOrWhiteSpace(a.Reason) || a.Reason.Trim().Length > 1000 ? "Phiếu thiếu lý do hợp lệ; cần rà soát hồ sơ."
            : batch.BatchStatus is "DISPOSED" or "CANCELLED" or "DELETED" ? "Lô đã loại bỏ hoặc bị hủy; không thể duyệt điều chỉnh."
            : !evidenceMatches ? "Tồn kho, phần giữ hàng hoặc phạm vi vị trí đã thay đổi so với lúc lập phiếu. Không thể dùng snapshot cũ để duyệt."
            : scope.Count == 0 || scope.Any(i => i.Unit != "kg") ? "Phạm vi tồn kho hoặc đơn vị kg không hợp lệ."
            : a.RequestKind == "COUNT" && scope.Any(i => i.WarehouseLocation.LocationStatus != "ACTIVE") ? "Vị trí kiểm đếm không còn hoạt động."
            : !PickingRules.Amount(a.ActualQuantity, true) || a.ActualQuantity == a.SystemQuantity ? "Lượng thực đếm hoặc chênh lệch không hợp lệ."
            : a.RequestKind == "COUNT" && a.ActualQuantity < scope.Single().ReservedQuantity ? "Không thể giảm tồn dưới lượng đang được giữ."
            : a.RequestKind == "DISPOSAL" && (a.SystemQuantity <= 0 || a.ActualQuantity != 0 || a.Lines.Any(l => l.ActualQuantity != 0)) ? "Hồ sơ loại bỏ không bao phủ đúng toàn bộ tồn lô."
            : a.RequestKind == "DISPOSAL" && (active || scope.Any(i => i.ReservedQuantity != 0)) ? "Lô còn reservation hiệu lực. Cần xử lý giữ hàng/đơn liên quan trước khi duyệt loại bỏ."
            : a.RequestKind == "DISPOSAL" && (draftIssues.Count > 0 || openTransfers.Count > 0 || draftReceipts.Count > 0) ? "Lô còn phiếu nhập/xuất nháp hoặc giao dịch chuyển kho chưa hoàn tất. Cần xử lý trước khi loại bỏ."
            : null;
        if (reason == null && a.RequestKind == "COUNT" && a.ActualQuantity > a.SystemQuantity)
        {
            try { await EnsureCountCapacityAsync(db, scope.Single(), a.ActualQuantity - a.SystemQuantity, ct); }
            catch (ConflictException e) { reason = e.Message; }
        }
        var detail = await ReadDetailAsync(db, id, ct);
        var version = await db.AuditLogs.AsNoTracking().Where(l => l.EntityName == "STOCK_ADJUSTMENT" && l.EntityId == id.ToString())
            .MaxAsync(l => (long?)l.AuditLogId, ct);
        var token = Hash(JsonSerializer.Serialize(new { a.StockAdjustmentId, a.AdjustmentCode, a.InventoryId, a.RequestKind,
            a.SystemQuantity, a.ActualQuantity, a.AdjustmentType, a.Reason, a.CreatedByAccountId, a.CreatedAt,
            a.InventorySnapshotJson, a.AdjustmentStatus, a.ReviewedByAccountId, a.ReviewedAt, a.ReviewNote,
            Lines = detail.Lines, Images = detail.Images, Current = currentJson, DraftIssues = draftIssues,
            OpenTransfers = openTransfers, DraftReceipts = draftReceipts, Version = version }));
        return new(detail, batch.BatchStatus, scope.Select(i => new AdjustmentStock(i.InventoryId, i.WarehouseLocationId,
            i.WarehouseLocation.LocationCode, i.WarehouseLocation.LocationStatus, i.QuantityOnHand, i.ReservedQuantity, i.LastUpdatedAt)).ToList(),
            reservations, token, reason == null, reason, a.AdjustmentStatus == "PENDING", hasSnapshot);
    }

    private static async Task EnsureCountCapacityAsync(AppDbContext db, Inventory stock, decimal increase, CancellationToken ct)
    {
        if (await db.Inventories.AnyAsync(i => i.Unit != "kg", ct)) throw new ConflictException("Tồn kho có đơn vị không tương thích với kg.");
        var local = await db.Inventories.Where(i => i.WarehouseLocationId == stock.WarehouseLocationId).SumAsync(i => (decimal?)i.QuantityOnHand, ct) ?? 0;
        var total = await db.Inventories.SumAsync(i => (decimal?)i.QuantityOnHand, ct) ?? 0;
        GoodsReceiptRules.EnsureCapacity(local, increase, stock.WarehouseLocation.MaxWeightKg, "Vị trí kho");
        foreach (var setting in await db.WarehouseSettings.AsNoTracking().ToListAsync(ct))
            GoodsReceiptRules.EnsureCapacity(total, increase, setting.MaxCapacityKg, "Kho");
    }
}
