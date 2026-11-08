using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    private static async Task<List<StockAdjustmentResolution>> CountScopeAsync(AppDbContext db, StockAdjustmentResolution r, CancellationToken ct)
    {
        if (r.PurchaseOrderId == null || r.StockAdjustment.RequestKind != "COUNT" || r.State == "REJECTED_HELD" && r.CoordinatingStockAdjustmentId == null) return [r];
        var rows = db.StockAdjustmentResolutions.Include(x => x.StockAdjustment).ThenInclude(a => a.Inventory).ThenInclude(i => i.ProductBatch)
            .Where(x => x.PurchaseOrderId == r.PurchaseOrderId && x.StockAdjustment.RequestKind == "COUNT"
                && x.StockAdjustment.Inventory.ProductBatchId == r.StockAdjustment.Inventory.ProductBatchId);
        rows = r.CoordinatingStockAdjustmentId is { } parent
            ? rows.Where(x => x.CoordinatingStockAdjustmentId == parent)
            : rows.Where(x => x.StockAdjustment.AdjustmentStatus == "PENDING" && x.State == "PENDING_MANAGER" && x.CoordinatingStockAdjustmentId == null);
        return await rows.OrderBy(x => x.StockAdjustmentId).ToListAsync(ct);
    }
    private static List<AdjustmentCountScope> CountValues(IEnumerable<StockAdjustmentResolution> rows) => rows.OrderBy(r => r.StockAdjustmentId)
        .Select(r => new AdjustmentCountScope(r.StockAdjustmentId, r.StockAdjustment.InventoryId, r.StockAdjustment.SystemQuantity, r.StockAdjustment.ActualQuantity, r.StockAdjustment.AdjustmentCode)).ToList();
    private static string CountSnapshot(IEnumerable<StockAdjustmentResolution> rows) => JsonSerializer.Serialize(CountValues(rows));
    private static async Task EnsureCountScopeAsync(AppDbContext db, StockAdjustmentResolution r, StockAdjustmentProposal? proposal, CancellationToken ct)
    {
        var scope = await CountScopeAsync(db, r, ct);
        if (scope.Count == 0 || scope.Any(x => x.StockAdjustment.AdjustmentStatus != "PENDING" || x.StockAdjustment.ActualQuantity >= x.StockAdjustment.SystemQuantity)
            || proposal != null && proposal.ScopeSnapshotJson != CountSnapshot(scope))
            throw new ConflictException("Phạm vi các phiếu kiểm đếm đã thay đổi; cần rà soát và gửi lại đúng phiên bản đề xuất.");
        var ids = scope.Select(x => x.StockAdjustmentId).ToArray();
        if (await db.StockAdjustments.AnyAsync(a => a.Inventory.ProductBatchId == r.StockAdjustment.Inventory.ProductBatchId
            && a.AdjustmentStatus == "PENDING" && !ids.Contains(a.StockAdjustmentId), ct))
            throw new ConflictException("Lô còn phiếu đang xử lý ngoài phạm vi đề xuất; cần rà soát trước khi quyết định.");
        if (await db.StockAdjustmentResolutions.AnyAsync(x => x.StockAdjustment.Inventory.ProductBatchId == r.StockAdjustment.Inventory.ProductBatchId
            && x.State == "REJECTED_HELD", ct)) throw new ConflictException("Lô còn phiếu bị từ chối chưa được kiểm tra lại hoặc thay thế.");
        foreach (var member in scope) await EnsureResolutionEvidenceAsync(db, member, ct);
    }
    private static void MoveCountScope(IEnumerable<StockAdjustmentResolution> scope, StockAdjustmentResolution root, string state, bool freeze)
    {
        foreach (var member in scope) {
            member.State = state; member.CoordinatingStockAdjustmentId = freeze ? root.StockAdjustmentId : null;
            if (member.StockAdjustmentId != root.StockAdjustmentId) { member.Version++; member.UpdatedAt = DateTime.UtcNow; }
        }
    }
}
