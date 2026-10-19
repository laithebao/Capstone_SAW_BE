using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsIssues;
using SAW.Application.Features.Picking;

namespace SAW.Application.Features.StockAdjustments;

public static class StockAdjustmentRules
{
    public const string DisposalLockReason = "Lô hàng đang chờ duyệt yêu cầu loại bỏ; không thể bán, giữ hàng, soạn hàng hoặc xuất kho.";
    public const string CountLockReason = "Lô hàng đang chờ duyệt điều chỉnh tồn; tạm dừng bán, giữ hàng, soạn hàng và xuất kho. Cần rà soát đơn/phần giữ hàng nếu khối lượng lô thay đổi.";
    public static readonly string[] Types = ["DAMAGE", "SPOILAGE", "COUNTING_ERROR", "OTHER"];
    public static void Scope(long id, string kind, long? inventoryId)
    {
        GoodsIssueRules.Id(id);
        if (kind is not ("COUNT" or "DISPOSAL") || inventoryId is <= 0 || (kind == "DISPOSAL" && inventoryId != null))
            throw new BadRequestException("Loại yêu cầu hoặc phạm vi tồn kho không hợp lệ.");
    }
    public static StockAdjustmentQuery Query(StockAdjustmentQuery q)
    {
        GoodsIssueRules.Page(q.Page, q.PageSize);
        if (q.ProductBatchId is <= 0 || q.WarehouseLocationId is <= 0 || q.RequestKind is not (null or "" or "COUNT" or "DISPOSAL")
            || (q.AdjustmentType is not (null or "") && !Types.Contains(q.AdjustmentType))
            || q.Status is not (null or "" or "PENDING" or "APPROVED" or "REJECTED") || q.FromDate > q.ToDate
            || q.FromDate == DateOnly.MinValue || q.ToDate == DateOnly.MaxValue
            || q.SortBy is not ("createdDesc" or "createdAsc" or "codeAsc" or "codeDesc"))
            throw new BadRequestException("Bộ lọc hoặc sắp xếp phiếu điều chỉnh không hợp lệ.");
        return q with { Search = GoodsIssueRules.Search(q.Search), Status = PickingRules.Text(q.Status), RequestKind = PickingRules.Text(q.RequestKind), AdjustmentType = PickingRules.Text(q.AdjustmentType) };
    }
    public static CreateStockAdjustmentRequest Create(CreateStockAdjustmentRequest r, int actor)
    {
        Scope(r.ProductBatchId, r.RequestKind, r.InventoryId);
        GoodsIssueRules.Operation(r.RequestId, actor, r.ExpectedSnapshot, true);
        if (!Types.Contains(r.AdjustmentType) || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length > 1000
            || r.ImageIds is null || r.ImageIds.Count > 5 || r.ImageIds.Any(x => x == Guid.Empty) || r.ImageIds.Distinct().Count() != r.ImageIds.Count
            || (r.RequestKind == "COUNT" && (r.InventoryId is null || r.ActualQuantityKg is null || !PickingRules.Amount(r.ActualQuantityKg.Value, true)))
            || (r.RequestKind == "DISPOSAL" && (r.ActualQuantityKg != null || r.AdjustmentType is not ("DAMAGE" or "SPOILAGE"))))
            throw new BadRequestException("Kiểm tra lượng thực đếm, loại yêu cầu, lý do (1–1000 ký tự) và tối đa 5 ảnh bằng chứng.");
        return r with { Reason = r.Reason.Trim(), ImageIds = r.ImageIds.Order().ToArray() };
    }
}

public sealed class StockAdjustmentService(IStockAdjustmentRepository repository)
{
    public Task<AdjustmentPage<AdjustmentSummary>> SearchAsync(StockAdjustmentQuery q, CancellationToken ct) => repository.SearchAsync(StockAdjustmentRules.Query(q), ct);
    public Task<AdjustmentDetail> GetAsync(long id, CancellationToken ct) { GoodsIssueRules.Id(id); return repository.GetAsync(id, ct); }
    public Task<IReadOnlyList<AdjustmentLocation>> LocationsAsync(CancellationToken ct) => repository.LocationsAsync(ct);
    public Task<AdjustmentPage<AdjustmentBatch>> BatchesAsync(string? search, int page, int pageSize, CancellationToken ct)
    { GoodsIssueRules.Page(page, pageSize); return repository.BatchesAsync(GoodsIssueRules.Search(search), page, pageSize, ct); }
    public Task<AdjustmentScope> ScopeAsync(long id, string kind, long? inventoryId, CancellationToken ct)
    { StockAdjustmentRules.Scope(id, kind, inventoryId); return repository.ScopeAsync(id, kind, inventoryId, ct); }
    public async Task<AdjustmentDetail> CreateAsync(CreateStockAdjustmentRequest r, int actor, CancellationToken ct)
    { var id = await repository.CreateAsync(StockAdjustmentRules.Create(r, actor), actor, ct); return await repository.GetAsync(id, ct); }
    public Task<AdjustmentImage> UploadAsync(byte[] content, int actor, CancellationToken ct)
    {
        if (actor <= 0) throw new UnauthorizedAccessException();
        if (content.Length is 0 or > 10485760) throw new BadRequestException("Mỗi ảnh tối đa 10 MB.");
        return repository.UploadAsync(content, actor, ct);
    }
    public Task RemoveUploadAsync(Guid id, int actor, CancellationToken ct)
    { if (actor <= 0) throw new UnauthorizedAccessException(); if (id == Guid.Empty) throw new BadRequestException("Mã ảnh không hợp lệ."); return repository.RemoveUploadAsync(id, actor, ct); }
}
