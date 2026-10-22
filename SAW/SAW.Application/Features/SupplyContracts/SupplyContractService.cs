using SAW.Application.Exceptions;

namespace SAW.Application.Features.SupplyContracts;

public sealed class SupplyContractService(ISupplyContractRepository repository) : ISupplyContractService
{
    private static readonly string[] Roles = ["WAREHOUSE_MANAGER", "SUPPLIER", "ADMINISTRATOR"];
    private static readonly string[] Statuses =
    [
        "PENDING_SUPPLIER", "SUPPLIER_REJECTED", "PENDING_ADMIN", "ADMIN_REJECTED", "APPROVED",
        "CANCELLATION_REQUESTED", "PENDING_ADMIN_CANCELLATION", "CANCELLED"
    ];
    private static readonly string[] Grades = ["A", "B", "C", "D", "E"];
    private static readonly string[] DeliveryPeriods = ["WEEK", "MONTH"];

    public Task<SupplyContractPage> ListAsync(
        int actorAccountId, string actorRole, SupplyContractQuery query, CancellationToken ct)
    {
        ValidateActor(actorAccountId, actorRole);
        if (query.Page < 1 || query.PageSize is not (10 or 20 or 50) || query.Search?.Length > 100)
            throw new BadRequestException("Bộ lọc hoặc phân trang hợp đồng không hợp lệ.");
        var status = Clean(query.Status)?.ToUpperInvariant();
        if (status is not null && !Statuses.Contains(status))
            throw new BadRequestException("Trạng thái hợp đồng không hợp lệ.");
        return repository.ListAsync(actorAccountId, actorRole,
            query with { Search = Clean(query.Search), Status = status }, ct);
    }

    public Task<SupplyContractDetail> GetAsync(
        int actorAccountId, string actorRole, long id, CancellationToken ct)
    {
        ValidateActor(actorAccountId, actorRole);
        ValidateId(id);
        return repository.GetAsync(actorAccountId, actorRole, id, ct);
    }

    public Task<SupplyContractOptions> GetOptionsAsync(CancellationToken ct) =>
        repository.GetOptionsAsync(ct);

    public Task<IReadOnlyList<SupplyContractBatchOption>> GetEligibleForBatchAsync(
        int supplierAccountId, DateOnly? expectedDeliveryDate, CancellationToken ct)
    {
        if (supplierAccountId <= 0) throw new UnauthorizedAccessException();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        return repository.GetEligibleForBatchAsync(supplierAccountId, expectedDeliveryDate ?? today, ct);
    }

    public Task<SupplyContractDetail> CreateAsync(
        int warehouseManagerAccountId, CreateSupplyContractRequest request, CancellationToken ct)
    {
        if (warehouseManagerAccountId <= 0 || request.SupplierId <= 0 || request.CropTypeId <= 0)
            throw new BadRequestException("Nhà cung cấp hoặc loại nông sản không hợp lệ.");
        var grade = Clean(request.QualityGrade)?.ToUpperInvariant();
        if (grade is null || !Grades.Contains(grade))
            throw new BadRequestException("Hạng chất lượng phải từ A đến E.");
        if (request.DeliveryQuantityKg <= 0)
            throw new BadRequestException("Sản lượng giao theo chu kỳ phải lớn hơn 0 kg.");
        var deliveryPeriod = Clean(request.DeliveryPeriod)?.ToUpperInvariant();
        if (deliveryPeriod is null || !DeliveryPeriods.Contains(deliveryPeriod))
            throw new BadRequestException("Chu kỳ giao phải là theo tuần hoặc theo tháng.");
        if (request.UnitPriceVndPerKg <= 0)
            throw new BadRequestException("Đơn giá thu mua phải lớn hơn 0.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        if (request.StartDate < today || request.EndDate < request.StartDate)
            throw new BadRequestException("Ngày bắt đầu không được ở quá khứ và ngày hết hạn phải sau ngày bắt đầu.");
        var terms = Clean(request.Terms);
        if (terms?.Length > 2000)
            throw new BadRequestException("Điều khoản hợp đồng không được vượt quá 2000 ký tự.");
        return repository.CreateAsync(warehouseManagerAccountId,
            request with
            {
                QualityGrade = grade,
                DeliveryPeriod = deliveryPeriod,
                Terms = terms
            }, ct);
    }

    public Task<SupplyContractDetail> ConfirmBySupplierAsync(
        int supplierAccountId, long id, CancellationToken ct)
    {
        ValidateAccountAndId(supplierAccountId, id);
        return repository.ConfirmBySupplierAsync(supplierAccountId, id, ct);
    }

    public Task<SupplyContractDetail> UpdateByWarehouseAsync(
        int warehouseManagerAccountId, long id, UpdateSupplyContractRequest request, CancellationToken ct)
    {
        ValidateAccountAndId(warehouseManagerAccountId, id);
        var grade = Clean(request.QualityGrade)?.ToUpperInvariant();
        if (grade is null || !Grades.Contains(grade))
            throw new BadRequestException("Hạng chất lượng phải từ A đến E.");
        if (request.DeliveryQuantityKg <= 0)
            throw new BadRequestException("Sản lượng giao theo chu kỳ phải lớn hơn 0 kg.");
        var deliveryPeriod = Clean(request.DeliveryPeriod)?.ToUpperInvariant();
        if (deliveryPeriod is null || !DeliveryPeriods.Contains(deliveryPeriod))
            throw new BadRequestException("Chu kỳ giao phải là theo tuần hoặc theo tháng.");
        if (request.UnitPriceVndPerKg <= 0)
            throw new BadRequestException("Đơn giá thu mua phải lớn hơn 0.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        if (request.EndDate < request.StartDate || request.EndDate < today)
            throw new BadRequestException("Ngày hết hạn phải từ hôm nay trở đi và không được trước ngày bắt đầu.");
        var terms = Clean(request.Terms);
        if (terms?.Length > 2000)
            throw new BadRequestException("Điều khoản hợp đồng không được vượt quá 2000 ký tự.");
        return repository.UpdateByWarehouseAsync(warehouseManagerAccountId, id,
            request with
            {
                QualityGrade = grade,
                DeliveryPeriod = deliveryPeriod,
                Terms = terms
            }, ct);
    }

    public Task<SupplyContractDetail> RequestCancellationBySupplierAsync(
        int supplierAccountId, long id, SupplyContractReasonRequest request, CancellationToken ct)
    {
        ValidateAccountAndId(supplierAccountId, id);
        return repository.RequestCancellationBySupplierAsync(
            supplierAccountId, id, ActionReason(request.Reason, "Lý do yêu cầu hủy"), ct);
    }

    public Task<SupplyContractDetail> CancelByWarehouseAsync(
        int warehouseManagerAccountId, long id, SupplyContractReasonRequest request, CancellationToken ct)
    {
        ValidateAccountAndId(warehouseManagerAccountId, id);
        return repository.CancelByWarehouseAsync(
            warehouseManagerAccountId, id, ActionReason(request.Reason, "Lý do hủy"), ct);
    }

    public Task<SupplyContractDetail> RejectCancellationByWarehouseAsync(
        int warehouseManagerAccountId, long id, SupplyContractReasonRequest request, CancellationToken ct)
    {
        ValidateAccountAndId(warehouseManagerAccountId, id);
        return repository.RejectCancellationByWarehouseAsync(
            warehouseManagerAccountId, id, ActionReason(request.Reason, "Lý do không duyệt yêu cầu hủy"), ct);
    }

    public Task<SupplyContractDetail> CancelByAdminAsync(
        int adminAccountId, long id, CancellationToken ct)
    {
        ValidateAccountAndId(adminAccountId, id);
        return repository.CancelByAdminAsync(adminAccountId, id, ct);
    }

    public Task<SupplyContractDetail> RejectBySupplierAsync(
        int supplierAccountId, long id, RejectSupplyContractRequest request, CancellationToken ct)
    {
        ValidateAccountAndId(supplierAccountId, id);
        return repository.RejectBySupplierAsync(supplierAccountId, id, RejectionReason(request), ct);
    }

    public Task<SupplyContractDetail> ApproveByAdminAsync(
        int adminAccountId, long id, CancellationToken ct)
    {
        ValidateAccountAndId(adminAccountId, id);
        return repository.ApproveByAdminAsync(adminAccountId, id, ct);
    }

    public Task<SupplyContractDetail> RejectByAdminAsync(
        int adminAccountId, long id, RejectSupplyContractRequest request, CancellationToken ct)
    {
        ValidateAccountAndId(adminAccountId, id);
        return repository.RejectByAdminAsync(adminAccountId, id, RejectionReason(request), ct);
    }

    private static void ValidateActor(int actorAccountId, string actorRole)
    {
        if (actorAccountId <= 0) throw new UnauthorizedAccessException();
        if (!Roles.Contains(actorRole)) throw new ForbiddenException();
    }

    private static void ValidateAccountAndId(int accountId, long id)
    {
        if (accountId <= 0) throw new UnauthorizedAccessException();
        ValidateId(id);
    }

    private static void ValidateId(long id)
    {
        if (id <= 0) throw new BadRequestException("Mã hợp đồng không hợp lệ.");
    }

    private static string RejectionReason(RejectSupplyContractRequest request)
        => ActionReason(request.Reason, "Lý do từ chối");

    private static string ActionReason(string? rawReason, string fieldName)
    {
        var reason = Clean(rawReason);
        if (reason is null || reason.Length is < 5 or > 1000)
            throw new BadRequestException($"{fieldName} phải có từ 5 đến 1000 ký tự.");
        return reason;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
