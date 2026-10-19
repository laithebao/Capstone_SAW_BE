using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsIssues;
using SAW.Application.Features.Picking;

namespace SAW.Application.Features.StockAdjustments;

public sealed class StockAdjustmentReviewService(IStockAdjustmentRepository repository)
{
    public Task<IReadOnlyList<AdjustmentLocation>> LocationsAsync(CancellationToken ct) => repository.LocationsAsync(ct);
    public Task<AdjustmentPage<AdjustmentSummary>> SearchAsync(StockAdjustmentQuery query, CancellationToken ct) =>
        repository.SearchAsync(StockAdjustmentRules.Query(query), ct);
    public Task<AdjustmentReviewDetail> GetAsync(long id, CancellationToken ct)
    { GoodsIssueRules.Id(id); return repository.GetReviewAsync(id, ct); }
    public async Task<AdjustmentReviewDetail> ReviewAsync(long id, ReviewStockAdjustmentRequest request, int actorId, CancellationToken ct)
    {
        GoodsIssueRules.Id(id);
        GoodsIssueRules.Operation(request.RequestId, actorId, request.ExpectedSnapshot, true);
        if (request.Decision is not ("APPROVE" or "REJECT") || request.ReviewNote?.Trim().Length > 1000
            || (request.Decision == "REJECT" && string.IsNullOrWhiteSpace(request.ReviewNote)))
            throw new BadRequestException("Quyết định không hợp lệ. Lý do từ chối bắt buộc; ghi chú tối đa 1000 ký tự.");
        await repository.ReviewAsync(id, request with { ReviewNote = PickingRules.Text(request.ReviewNote) }, actorId, ct);
        return await repository.GetReviewAsync(id, ct);
    }
}
