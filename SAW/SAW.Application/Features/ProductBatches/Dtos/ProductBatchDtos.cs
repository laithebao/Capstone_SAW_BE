namespace SAW.Application.Features.ProductBatches.Dtos;

public sealed record ProductBatchQuery(
    string? BatchCode, int? SupplierId, int? CropTypeId, string? Status,
    string? SortBy, int Page = 1, int PageSize = 20);

public sealed record ProductBatchListItem(
    long Id, string BatchCode, string ProductName,
    int SupplierId, string SupplierName,
    int CropTypeId, string CropTypeName, string CategoryName,
    decimal Quantity, string Unit, DateTime CreatedAt, DateTime? UpdatedAt,
    string BatchStatus);

public sealed record ProductBatchListResponse(
    IReadOnlyList<ProductBatchListItem> Items, int TotalCount, int Page, int PageSize);

public sealed record ProductBatchDetail(
    long Id, string BatchCode, string ProductName,
    int SupplierId, string SupplierName,
    int CropTypeId, string CropTypeName, string CategoryName,
    decimal Quantity, string Unit, decimal WeightInKg,
    decimal? VerifiedQuantity, decimal? VerifiedWeightInKg, DateOnly HarvestDate,
    string GrowingAreaName, DateOnly? ExpectedDeliveryDate, DateOnly? ExpiryDate,
    DateTime CreatedAt, DateTime? UpdatedAt, string BatchStatus, string? Note,
    string? PackagingType, int? PackageCount, decimal? PackageUnitWeightKg,
    string? VerifiedPackagingType, int? VerifiedPackageCount,
    decimal? VerifiedPackageUnitWeightKg, string? ReceivingNote, string? RejectionReason,
    decimal? ExpectedMinTempC, decimal? ExpectedMaxTempC,
    decimal? ExpectedMinHumidityPct, decimal? ExpectedMaxHumidityPct,
    int? ShelfLifeDaysSnapshot,
    bool CanUpdateReceivingInformation, string? ReceivingUpdateLockReason)
{
    public ProductBatchQcSummary? LatestQcInspection { get; init; }
    public IReadOnlyList<ProductBatchGoodsReceipt> GoodsReceipts { get; init; } = [];
    public IReadOnlyList<ProductBatchStatusHistoryItem> StatusHistory { get; init; } = [];
    public IReadOnlyList<ProductBatchWarehouseTransaction> WarehouseTransactions { get; init; } = [];
}

public sealed record ProductBatchQcSummary(long Id, string InspectionCode, string InspectionStatus,
    string? QcResult, string? QualityGrade, DateTime StartedAt, DateTime? CompletedAt,
    string? QcStaffName, string? Note);

public sealed record ProductBatchGoodsReceipt(long Id, string ReceiptCode, string ReceiptStatus,
    DateTime ReceivedAt, DateTime? CommittedAt, int WarehouseLocationId,
    string? LocationName, string? OperationStaffName, string? Note,
    decimal ReceivedQuantity, string Unit, decimal WeightInKg);

public sealed record ProductBatchStatusHistoryItem(long Id, DateTime ChangedAt, string? OldStatus,
    string NewStatus, string? ChangedByName, string? ChangeReason);

// These are saved warehouse documents, including their actual status, not inferred stock movements.
public sealed record ProductBatchWarehouseTransaction(long Id, string TransactionType, string Code,
    string Status, DateTime RecordedAt, decimal Quantity, string Unit, decimal? WeightInKg,
    string? FromLocationName, string? ToLocationName, string? PerformedByName, string? Note);

public sealed record ProductBatchRelatedInformation(ProductBatchQcSummary? LatestQcInspection,
    IReadOnlyList<ProductBatchGoodsReceipt> GoodsReceipts,
    IReadOnlyList<ProductBatchStatusHistoryItem> StatusHistory,
    IReadOnlyList<ProductBatchWarehouseTransaction> WarehouseTransactions)
{
    public static ProductBatchRelatedInformation Empty { get; } = new(null, [], [], []);
}

public sealed record ProductBatchFilterOption(int Id, string Name);

public sealed record ProductBatchFilterOptions(
    IReadOnlyList<ProductBatchFilterOption> Suppliers,
    IReadOnlyList<ProductBatchFilterOption> CropTypes,
    IReadOnlyList<string> Statuses);
