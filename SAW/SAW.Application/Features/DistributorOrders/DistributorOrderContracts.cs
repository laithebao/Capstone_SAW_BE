namespace SAW.Application.Features.DistributorOrders;

public sealed record DistributorQuery(string? Search = null, string? Status = null,
    DateOnly? FromDate = null, DateOnly? ToDate = null, int Page = 1, int PageSize = 10);
public sealed record DistributorPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record CatalogLot(long BatchId, string BatchCode, string ProductName, string CropName,
    string SupplierName, string Origin, string? QualityGrade, DateOnly HarvestDate,
    DateOnly? ExpiryDate, decimal WeightKg, decimal WholeLotPrice);
public sealed record DistributorLotDetail(long BatchId, string BatchCode, string ProductName, string CropName,
    string CategoryName, string SupplierName, string Origin, string Province, string District, string Ward,
    string? QualityGrade, DateOnly HarvestDate, DateOnly? ExpiryDate, decimal WeightKg, decimal? WholeLotPrice,
    string BatchStatus, bool CanPurchase, string? PackagingType, int? PackageCount, decimal? PackageUnitWeightKg,
    decimal? MinTempC, decimal? MaxTempC, decimal? MinHumidityPct, decimal? MaxHumidityPct,
    string? QcResult, DateTime? QcCompletedAt, string? InspectionStandardName, int? InspectionStandardVersionNo);
public sealed record SelectedLot(long BatchId, decimal ExpectedPrice, decimal ExpectedWeightKg);
public sealed record CreateDistributorOrderRequest(Guid RequestId, List<SelectedLot> Lots,
    string DeliveryAddress, string ContactPhone, DateOnly? ExpectedDeliveryDate, string? Note);
public sealed record DistributorOrderLine(long BatchId, string BatchCode, string ProductName,
    decimal WeightKg, decimal WholeLotPrice);
public sealed record DistributorOrderEvent(string? OldStatus, string NewStatus, DateTime ChangedAt, string? Reason);
public sealed record DistributorOrderSummary(long Id, string OrderCode, string Status, int LotCount,
    decimal TotalAmount, DateTime CreatedAt, DateOnly? ExpectedDeliveryDate);
public sealed record DistributorOrderDetail(long Id, string OrderCode, string Status,
    string DeliveryAddress, string? ContactPhone, DateOnly? ExpectedDeliveryDate, string? Note,
    decimal TotalAmount, DateTime CreatedAt, DateTime? ApprovedAt, string? RejectionReason,
    string? CancellationReason, DateTime? ReceivedAt, bool CanCancel, bool CanConfirmReceipt,
    IReadOnlyList<DistributorOrderLine> Lines, IReadOnlyList<DistributorOrderEvent> History);

public interface IDistributorOrderRepository
{
    Task<DistributorDashboard> DashboardAsync(int accountId, CancellationToken ct);
    Task<DistributorPage<CatalogLot>> CatalogAsync(int accountId, DistributorQuery query, CancellationToken ct);
    Task<DistributorLotDetail> LotAsync(int accountId, long id, CancellationToken ct);
    Task<DistributorPage<DistributorOrderSummary>> ListAsync(int accountId, DistributorQuery query, CancellationToken ct);
    Task<DistributorOrderDetail> GetAsync(int accountId, long id, CancellationToken ct);
    Task<DistributorOrderDetail> CreateAsync(int accountId, CreateDistributorOrderRequest request, CancellationToken ct);
    Task<DistributorOrderDetail> CancelAsync(int accountId, long id, CancellationToken ct);
    Task<DistributorOrderDetail> ConfirmReceiptAsync(int accountId, long id, CancellationToken ct);
}

public interface IDistributorOrderService : IDistributorOrderRepository { }

public sealed record DistributorDashboard(int PendingOrders, int SuccessfulOrders, int CancelledOrders,
    decimal TotalSpent, IReadOnlyList<DistributorOrderSummary> RecentOrders);
