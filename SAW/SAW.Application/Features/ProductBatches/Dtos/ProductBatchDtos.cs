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
    decimal Quantity, string Unit, DateOnly HarvestDate,
    string GrowingAreaName, DateOnly? ExpectedDeliveryDate, DateOnly? ExpiryDate,
    DateTime CreatedAt, DateTime? UpdatedAt, string BatchStatus, string? Note);

public sealed record ProductBatchFilterOption(int Id, string Name);

public sealed record ProductBatchFilterOptions(
    IReadOnlyList<ProductBatchFilterOption> Suppliers,
    IReadOnlyList<ProductBatchFilterOption> CropTypes,
    IReadOnlyList<string> Statuses);
