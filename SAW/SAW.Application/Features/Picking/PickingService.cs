using SAW.Application.Exceptions;

namespace SAW.Application.Features.Picking;

public static class PickingRules
{
    public static readonly string[] ActiveReservations = ["RESERVED", "PARTIALLY_PICKED", "PICKED"];
    public static readonly string[] WorkStatuses = ["APPROVED", "RESERVED", "PARTIALLY_PICKED"];
    public static bool Amount(decimal value, bool allowZero = false) => value >= (allowZero ? 0 : 0.001m)
        && value <= 999999999999999.999m && decimal.Truncate(value * 1000) == value * 1000;
    public static string? BatchLockReason(string status, DateOnly? expiry) =>
        expiry < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)) ? "Lô hàng đã hết hạn sử dụng."
        : status is not ("IN_STOCK" or "RESERVED" or "PARTIALLY_ISSUED") ? "Trạng thái lô không cho phép xử lý kho."
        : null;
    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static PickingQuery Validate(PickingQuery query)
    {
        var search = Text(query.Search); var status = Text(query.Status)?.ToUpperInvariant();
        if (search?.Length > 100 || query.Page is < 1 or > 100000 || query.PageSize is < 1 or > 100
            || (status is not null && !WorkStatuses.Contains(status))
            || query.Sort is not ("approved_desc" or "approved_asc" or "delivery_asc" or "delivery_desc")
            || query.FromDate > query.ToDate || query.FromDate == DateOnly.MinValue || query.ToDate == DateOnly.MaxValue)
            throw new BadRequestException("Bộ lọc, phân trang hoặc sắp xếp đơn soạn hàng không hợp lệ.");
        return query with { Search = search, Status = status };
    }
}

public sealed class PickingService(IPickingRepository repository) : IPickingService
{
    public Task<PickingPage<PickingOrderSummary>> SearchAsync(PickingQuery query, CancellationToken ct) => repository.SearchAsync(PickingRules.Validate(query), ct);
    public Task<PickingOrderDetail> GetAsync(long id, CancellationToken ct) => id > 0 ? repository.GetAsync(id, ct)
        : throw new BadRequestException("Mã đơn không hợp lệ.");
    public async Task<PickingOrderDetail> ConfirmAsync(long orderId, ConfirmPickingRequest request, int actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new UnauthorizedAccessException();
        if (orderId <= 0 || request.RequestId == Guid.Empty || request.ReservationId <= 0 || request.OrderDetailId <= 0
            || request.InventoryId <= 0 || request.ProductBatchId <= 0 || !PickingRules.Amount(request.QuantityKg)
            || !PickingRules.Amount(request.ExpectedPickedQuantityKg, true) || request.Note?.Trim().Length > 1000)
            throw new BadRequestException("Dữ liệu soạn hàng không hợp lệ. Khối lượng phải lớn hơn 0 và có tối đa 3 số thập phân; ghi chú tối đa 1000 ký tự.");
        await repository.ConfirmAsync(orderId, request with { Note = PickingRules.Text(request.Note) }, actorId, ct);
        return await repository.GetAsync(orderId, ct);
    }
}
