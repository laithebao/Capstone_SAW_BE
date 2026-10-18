using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;

namespace SAW.Application.Features.GoodsIssues;

public static class GoodsIssueRules
{
    public static readonly string[] OrderStatuses = ["PARTIALLY_PICKED", "PICKED", "PARTIALLY_ISSUED"];
    public static void Id(long id) { if (id <= 0) throw new BadRequestException("Mã phiếu hoặc đơn hàng không hợp lệ."); }
    public static void Page(int page, int size)
    { if (page is < 1 or > 100000 || size is < 1 or > 100) throw new BadRequestException("Phân trang không hợp lệ."); }
    public static string? Search(string? search)
    {
        if (search?.Length > 100 || search?.Any(char.IsControl) == true) throw new BadRequestException("Từ khóa tối đa 100 ký tự và không chứa ký tự điều khiển.");
        return PickingRules.Text(search);
    }
    public static GoodsIssueQuery Query(GoodsIssueQuery query)
    {
        Page(query.Page, query.PageSize);
        if (query.Status is not (null or "" or "DRAFT" or "COMMITTED" or "CANCELLED") || query.WarehouseLocationId is <= 0
            || query.FromDate > query.ToDate || query.ToDate == DateOnly.MaxValue
            || query.SortBy is not ("issuedAtDesc" or "issuedAtAsc" or "idDesc" or "idAsc"))
            throw new BadRequestException("Bộ lọc hoặc sắp xếp phiếu xuất không hợp lệ.");
        return query with { Search = Search(query.Search), Status = PickingRules.Text(query.Status) };
    }
    public static void Operation(Guid requestId, int actorId, string? snapshot = null, bool requireSnapshot = false)
    {
        if (actorId <= 0) throw new UnauthorizedAccessException();
        if (requestId == Guid.Empty || (requireSnapshot && (snapshot?.Length != 64 || !snapshot.All(Uri.IsHexDigit))))
            throw new BadRequestException("Thiếu mã thao tác hoặc phiên bản phiếu đã xem. Vui lòng tải lại.");
    }
    public static void Draft(DateOnly date, string? receiver, string? note, IReadOnlyList<GoodsIssueLineRequest>? lines)
    {
        if (date == default || date > DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)))
            throw new BadRequestException("Ngày xuất không hợp lệ hoặc ở tương lai.");
        if (receiver?.Trim().Length > 200 || note?.Trim().Length > 1000)
            throw new BadRequestException("Người nhận tối đa 200 ký tự; ghi chú tối đa 1000 ký tự.");
        if (lines is null || lines.Count is < 1 or > 100 || lines.Any(l => l is null || l.ReservationId <= 0 || !PickingRules.Amount(l.WeightInKg))
            || lines.Select(l => l.ReservationId).Distinct().Count() != lines.Count)
            throw new BadRequestException("Chọn 1–100 phần giữ hàng không trùng. Khối lượng phải dương, đúng decimal(18,3).");
    }
}

public sealed class GoodsIssueService(IGoodsIssueRepository repository)
{
    public Task<GoodsIssuePage<GoodsIssueSummary>> SearchAsync(GoodsIssueQuery query, CancellationToken ct) => repository.SearchAsync(GoodsIssueRules.Query(query), ct);
    public Task<IReadOnlyList<GoodsIssueLocation>> LocationsAsync(CancellationToken ct) => repository.LocationsAsync(ct);
    public Task<GoodsIssuePage<GoodsIssueOrder>> EligibleAsync(string? search, int page, int pageSize, CancellationToken ct)
    { GoodsIssueRules.Page(page, pageSize); return repository.EligibleAsync(GoodsIssueRules.Search(search), page, pageSize, ct); }
    public Task<GoodsIssueOrder> OrderAsync(long id, CancellationToken ct) { GoodsIssueRules.Id(id); return repository.OrderAsync(id, ct); }
    public Task<GoodsIssueDetail> GetAsync(long id, CancellationToken ct) { GoodsIssueRules.Id(id); return repository.GetAsync(id, ct); }
    public async Task<GoodsIssueDetail> CreateAsync(CreateGoodsIssueRequest request, int actorId, CancellationToken ct)
    {
        GoodsIssueRules.Operation(request.RequestId, actorId); GoodsIssueRules.Id(request.PurchaseOrderId);
        GoodsIssueRules.Draft(request.IssuedDate, request.ReceiverName, request.Note, request.Lines);
        var id = await repository.CreateAsync(request with { ReceiverName = PickingRules.Text(request.ReceiverName), Note = PickingRules.Text(request.Note) }, actorId, ct);
        return await repository.GetAsync(id, ct);
    }
    public async Task<GoodsIssueDetail> UpdateDraftAsync(long id, UpdateGoodsIssueDraftRequest request, int actorId, CancellationToken ct)
    {
        GoodsIssueRules.Id(id); GoodsIssueRules.Operation(request.RequestId, actorId, request.ExpectedSnapshot, true);
        GoodsIssueRules.Draft(request.IssuedDate, request.ReceiverName, request.Note, request.Lines);
        await repository.UpdateDraftAsync(id, request with { ReceiverName = PickingRules.Text(request.ReceiverName), Note = PickingRules.Text(request.Note) }, actorId, ct);
        return await repository.GetAsync(id, ct);
    }
    public async Task<GoodsIssueDetail> ConfirmAsync(long id, ConfirmGoodsIssueRequest request, int actorId, CancellationToken ct)
    {
        GoodsIssueRules.Id(id); GoodsIssueRules.Operation(request.RequestId, actorId, request.ExpectedSnapshot, true);
        await repository.ConfirmAsync(id, request, actorId, ct); return await repository.GetAsync(id, ct);
    }
}
