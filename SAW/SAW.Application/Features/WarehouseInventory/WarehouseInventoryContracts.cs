namespace SAW.Application.Features.WarehouseInventory;

public sealed record InventoryLocationLevel(
    int LocationId,
    string LocationCode,
    string ZoneName,
    string? RackName,
    string? BinName,
    string LocationStatus,
    decimal QuantityOnHandKg,
    decimal ReservedQuantityKg,
    decimal AvailableQuantityKg,
    decimal? MaxWeightKg,
    decimal? UtilizationPercent,
    int BatchCount,
    int LowStockItemCount,
    DateTime? LastUpdatedAt);

public sealed record WarehouseInventoryLevelChart(
    decimal TotalOnHandKg,
    decimal TotalAvailableKg,
    decimal TotalReservedKg,
    int ActiveLocationCount,
    int LowStockItemCount,
    DateTime GeneratedAt,
    IReadOnlyList<InventoryLocationLevel> Locations);

public sealed record WarehouseCapacityLocation(
    int LocationId,
    string LocationCode,
    string ZoneName,
    decimal UsedWeightKg,
    decimal? MaxWeightKg,
    decimal? AvailableWeightKg,
    decimal? UtilizationPercent,
    string Status);

public sealed record WarehouseCapacityChart(
    decimal UsedWeightKg,
    decimal ConfiguredCapacityKg,
    decimal AvailableWeightKg,
    decimal UtilizationPercent,
    int NearCapacityCount,
    int OvercrowdedCount,
    DateTime GeneratedAt,
    IReadOnlyList<WarehouseCapacityLocation> Locations);

public sealed record ProductQualityDistributionItem(
    string Grade,
    int BatchCount,
    decimal QuantityOnHandKg,
    decimal Percentage);

public sealed record ProductQualityDistributionChart(
    int TotalBatchCount,
    decimal TotalQuantityOnHandKg,
    int GradedBatchCount,
    int UngradedBatchCount,
    string? LeadingGrade,
    DateTime GeneratedAt,
    IReadOnlyList<ProductQualityDistributionItem> Grades);

public sealed record WarehouseDistributorOrderLine(
    long OrderDetailId, string ProductName, string? BatchCode, decimal RequestedWeightKg,
    decimal AvailableWeightKg, decimal UnitPrice, bool StockAvailable);

public sealed record WarehouseDistributorOrderSummary(
    long Id, string OrderCode, string Status, string DistributorName, int LineCount,
    decimal TotalAmount, DateTime CreatedAt, bool StockAvailable);

public sealed record WarehouseDistributorOrderPage(
    IReadOnlyList<WarehouseDistributorOrderSummary> Items, int TotalCount, int Page, int PageSize);

public sealed record WarehouseDistributorOrderDetail(
    long Id, string OrderCode, string Status, string DistributorName, decimal TotalAmount,
    DateTime CreatedAt, DateOnly? ExpectedDeliveryDate, bool StockAvailable,
    IReadOnlyList<WarehouseDistributorOrderLine> Lines);

public interface IWarehouseInventoryRepository
{
    Task<WarehouseInventoryLevelChart> GetLevelChartAsync(CancellationToken ct);
    Task<WarehouseCapacityChart> GetCapacityChartAsync(CancellationToken ct);
    Task<ProductQualityDistributionChart> GetQualityDistributionChartAsync(CancellationToken ct);
    Task<WarehouseDistributorOrderPage> GetDistributorOrdersAsync(string? status, int page, int pageSize, CancellationToken ct);
    Task<WarehouseDistributorOrderDetail> GetDistributorOrderAsync(long id, CancellationToken ct);
    Task<WarehouseDistributorOrderDetail> ApproveDistributorOrderAsync(int actorAccountId, long id, CancellationToken ct);
}

public interface IWarehouseInventoryService
{
    Task<WarehouseInventoryLevelChart> GetLevelChartAsync(CancellationToken ct);
    Task<WarehouseCapacityChart> GetCapacityChartAsync(CancellationToken ct);
    Task<ProductQualityDistributionChart> GetQualityDistributionChartAsync(CancellationToken ct);
    Task<WarehouseDistributorOrderPage> GetDistributorOrdersAsync(string? status, int page, int pageSize, CancellationToken ct);
    Task<WarehouseDistributorOrderDetail> GetDistributorOrderAsync(long id, CancellationToken ct);
    Task<WarehouseDistributorOrderDetail> ApproveDistributorOrderAsync(int actorAccountId, long id, CancellationToken ct);
}
