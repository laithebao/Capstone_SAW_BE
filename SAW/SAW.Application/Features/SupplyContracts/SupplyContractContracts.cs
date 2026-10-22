namespace SAW.Application.Features.SupplyContracts;

public sealed record SupplyContractQuery(
    string? Search = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 20);

public sealed record SupplyContractSummary(
    long Id,
    string ContractCode,
    string SupplierName,
    string CropTypeName,
    string QualityGrade,
    decimal DeliveryQuantityKg,
    string DeliveryPeriod,
    decimal UnitPriceVndPerKg,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    int Revision,
    DateTime CreatedAt);

public sealed record SupplyContractDetail(
    long Id,
    string ContractCode,
    int SupplierId,
    string SupplierName,
    int CropTypeId,
    string CropTypeName,
    string QualityGrade,
    decimal DeliveryQuantityKg,
    string DeliveryPeriod,
    decimal UnitPriceVndPerKg,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Terms,
    string Status,
    int Revision,
    string WarehouseManagerName,
    DateTime CreatedAt,
    DateTime? SupplierRespondedAt,
    string? SupplierRejectionReason,
    string? AdminName,
    DateTime? AdminReviewedAt,
    string? AdminRejectionReason,
    DateTime? CancellationRequestedAt,
    string? CancellationRequestReason,
    DateTime? CancelledAt,
    string? CancellationReason);

public sealed record SupplyContractPage(
    IReadOnlyList<SupplyContractSummary> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record SupplyContractSupplierOption(
    int Id, string Code, string Name, IReadOnlyList<int> CropTypeIds);
public sealed record SupplyContractCropTypeOption(int Id, string Code, string Name, string CategoryName);
public sealed record SupplyContractOptions(
    IReadOnlyList<SupplyContractSupplierOption> Suppliers,
    IReadOnlyList<SupplyContractCropTypeOption> CropTypes,
    IReadOnlyList<string> QualityGrades);

public sealed record SupplyContractBatchOption(
    long Id,
    string ContractCode,
    int CropTypeId,
    string CropTypeName,
    string CategoryName,
    string QualityGrade,
    decimal DeliveryQuantityKg,
    decimal UsedQuantityKg,
    decimal RemainingQuantityKg,
    string DeliveryPeriod,
    decimal UnitPriceVndPerKg,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly QuotaPeriodStartDate,
    DateOnly QuotaPeriodEndDate);

public sealed record CreateSupplyContractRequest(
    int SupplierId,
    int CropTypeId,
    string QualityGrade,
    decimal DeliveryQuantityKg,
    string DeliveryPeriod,
    decimal UnitPriceVndPerKg,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Terms);

public sealed record UpdateSupplyContractRequest(
    string QualityGrade,
    decimal DeliveryQuantityKg,
    string DeliveryPeriod,
    decimal UnitPriceVndPerKg,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Terms);

public sealed record RejectSupplyContractRequest(string Reason);
public sealed record SupplyContractReasonRequest(string Reason);

public interface ISupplyContractRepository
{
    Task<SupplyContractPage> ListAsync(
        int actorAccountId, string actorRole, SupplyContractQuery query, CancellationToken ct);
    Task<SupplyContractDetail> GetAsync(
        int actorAccountId, string actorRole, long id, CancellationToken ct);
    Task<SupplyContractOptions> GetOptionsAsync(CancellationToken ct);
    Task<IReadOnlyList<SupplyContractBatchOption>> GetEligibleForBatchAsync(
        int supplierAccountId, DateOnly expectedDeliveryDate, CancellationToken ct);
    Task<SupplyContractDetail> CreateAsync(
        int warehouseManagerAccountId, CreateSupplyContractRequest request, CancellationToken ct);
    Task<SupplyContractDetail> UpdateByWarehouseAsync(
        int warehouseManagerAccountId, long id, UpdateSupplyContractRequest request, CancellationToken ct);
    Task<SupplyContractDetail> RequestCancellationBySupplierAsync(
        int supplierAccountId, long id, string reason, CancellationToken ct);
    Task<SupplyContractDetail> CancelByWarehouseAsync(
        int warehouseManagerAccountId, long id, string reason, CancellationToken ct);
    Task<SupplyContractDetail> RejectCancellationByWarehouseAsync(
        int warehouseManagerAccountId, long id, string reason, CancellationToken ct);
    Task<SupplyContractDetail> CancelByAdminAsync(
        int adminAccountId, long id, CancellationToken ct);
    Task<SupplyContractDetail> ConfirmBySupplierAsync(
        int supplierAccountId, long id, CancellationToken ct);
    Task<SupplyContractDetail> RejectBySupplierAsync(
        int supplierAccountId, long id, string reason, CancellationToken ct);
    Task<SupplyContractDetail> ApproveByAdminAsync(
        int adminAccountId, long id, CancellationToken ct);
    Task<SupplyContractDetail> RejectByAdminAsync(
        int adminAccountId, long id, string reason, CancellationToken ct);
}

public interface ISupplyContractService
{
    Task<SupplyContractPage> ListAsync(
        int actorAccountId, string actorRole, SupplyContractQuery query, CancellationToken ct);
    Task<SupplyContractDetail> GetAsync(
        int actorAccountId, string actorRole, long id, CancellationToken ct);
    Task<SupplyContractOptions> GetOptionsAsync(CancellationToken ct);
    Task<IReadOnlyList<SupplyContractBatchOption>> GetEligibleForBatchAsync(
        int supplierAccountId, DateOnly? expectedDeliveryDate, CancellationToken ct);
    Task<SupplyContractDetail> CreateAsync(
        int warehouseManagerAccountId, CreateSupplyContractRequest request, CancellationToken ct);
    Task<SupplyContractDetail> UpdateByWarehouseAsync(
        int warehouseManagerAccountId, long id, UpdateSupplyContractRequest request, CancellationToken ct);
    Task<SupplyContractDetail> RequestCancellationBySupplierAsync(
        int supplierAccountId, long id, SupplyContractReasonRequest request, CancellationToken ct);
    Task<SupplyContractDetail> CancelByWarehouseAsync(
        int warehouseManagerAccountId, long id, SupplyContractReasonRequest request, CancellationToken ct);
    Task<SupplyContractDetail> RejectCancellationByWarehouseAsync(
        int warehouseManagerAccountId, long id, SupplyContractReasonRequest request, CancellationToken ct);
    Task<SupplyContractDetail> CancelByAdminAsync(
        int adminAccountId, long id, CancellationToken ct);
    Task<SupplyContractDetail> ConfirmBySupplierAsync(
        int supplierAccountId, long id, CancellationToken ct);
    Task<SupplyContractDetail> RejectBySupplierAsync(
        int supplierAccountId, long id, RejectSupplyContractRequest request, CancellationToken ct);
    Task<SupplyContractDetail> ApproveByAdminAsync(
        int adminAccountId, long id, CancellationToken ct);
    Task<SupplyContractDetail> RejectByAdminAsync(
        int adminAccountId, long id, RejectSupplyContractRequest request, CancellationToken ct);
}
