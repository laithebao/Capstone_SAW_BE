using SAW.Application.Exceptions;

namespace SAW.Application.Features.DistributorOrders;

public sealed class DistributorOrderService(IDistributorOrderRepository repository) : IDistributorOrderService
{
    public Task<DistributorDashboard> DashboardAsync(int accountId, CancellationToken ct) => repository.DashboardAsync(accountId, ct);
    public static readonly string[] Statuses = ["PENDING", "APPROVED", "RESERVED", "PARTIALLY_PICKED",
        "PICKED", "PREPARED", "PARTIALLY_ISSUED", "ISSUED", "DISPATCHED", "DELIVERED", "REJECTED", "CANCELLED"];

    public static void ValidateQuery(DistributorQuery query)
    {
        if (query.Page < 1 || query.PageSize is not (10 or 20 or 50)
            || (long)(query.Page - 1) * query.PageSize > int.MaxValue
            || query.Search?.Length > 100 || query.Search?.Any(char.IsControl) == true
            || query.FromDate > query.ToDate || query.ToDate == DateOnly.MaxValue
            || (!string.IsNullOrEmpty(query.Status) && !Statuses.Contains(query.Status)))
            throw new BadRequestException("Bộ lọc hoặc phân trang không hợp lệ.");
    }

    public static CreateDistributorOrderRequest ValidateCreate(CreateDistributorOrderRequest request, DateOnly today)
    {
        if (request.RequestId == Guid.Empty || request.Lots is null || request.Lots.Count is < 1 or > 20
            || request.Lots.Any(l => l is null || l.BatchId <= 0 || l.ExpectedPrice <= 0 || l.ExpectedWeightKg <= 0)
            || request.Lots.Select(l => l.BatchId).Distinct().Count() != request.Lots.Count)
            throw new BadRequestException("Chọn từ 1 đến 20 lô khác nhau để đặt mua nguyên lô.");
        var address = request.DeliveryAddress?.Trim();
        var phone = request.ContactPhone?.Trim();
        var note = request.Note?.Trim();
        if (string.IsNullOrEmpty(phone) || phone.Length is < 10 or > 30 || phone.Any(c => c is < '0' or > '9'))
            throw new BadRequestException("Số điện thoại phải có từ 10 đến 30 chữ số, không chứa chữ hoặc ký tự khác.");
        if (string.IsNullOrWhiteSpace(address) || address.Length > 500 || address.Any(char.IsControl)
            || note?.Length > 1000 || (request.ExpectedDeliveryDate is { } requestedDate && requestedDate < today))
            throw new BadRequestException("Địa chỉ hoặc ngày mong muốn nhận hàng không hợp lệ.");
        return request with { DeliveryAddress = address, ContactPhone = phone, Note = string.IsNullOrEmpty(note) ? null : note };
    }

    public Task<DistributorPage<CatalogLot>> CatalogAsync(int accountId, DistributorQuery query, CancellationToken ct)
    { ValidateQuery(query); return repository.CatalogAsync(accountId, query with { Search = query.Search?.Trim() }, ct); }
    public Task<DistributorLotDetail> LotAsync(int accountId, long id, CancellationToken ct)
    { ValidateId(id); return repository.LotAsync(accountId, id, ct); }
    public Task<DistributorPage<DistributorOrderSummary>> ListAsync(int accountId, DistributorQuery query, CancellationToken ct)
    { ValidateQuery(query); return repository.ListAsync(accountId, query with { Search = query.Search?.Trim() }, ct); }
    public Task<DistributorOrderDetail> GetAsync(int accountId, long id, CancellationToken ct)
    { ValidateId(id); return repository.GetAsync(accountId, id, ct); }
    public Task<DistributorOrderDetail> CreateAsync(int accountId, CreateDistributorOrderRequest request, CancellationToken ct) =>
        repository.CreateAsync(accountId, ValidateCreate(request, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7))), ct);
    public Task<DistributorOrderDetail> CancelAsync(int accountId, long id, CancellationToken ct)
    { ValidateId(id); return repository.CancelAsync(accountId, id, ct); }
    public Task<DistributorOrderDetail> ConfirmReceiptAsync(int accountId, long id, CancellationToken ct)
    { ValidateId(id); return repository.ConfirmReceiptAsync(accountId, id, ct); }
    private static void ValidateId(long id)
    { if (id <= 0) throw new BadRequestException("Mã đơn hàng không hợp lệ."); }
}
