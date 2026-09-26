namespace SAW.Application.Features.ProductBatches.Dtos;

public sealed record SubmittedDeclarationOption(
    long Id, string BatchCode, string ProductName, decimal DeclaredQuantity, string Unit);

public sealed record SubmittedDeclarationDetail(
    long Id, string BatchCode, int SupplierId, string SupplierName,
    int CropTypeId, string CropTypeName, string ProductName,
    int GrowingAreaId, string GrowingAreaName, DateOnly HarvestDate,
    decimal DeclaredQuantity, string Unit, decimal WeightInKg,
    string? PackagingType, int? PackageCount, decimal? PackageUnitWeightKg,
    decimal? ExpectedMinTempC, decimal? ExpectedMaxTempC,
    decimal? ExpectedMinHumidityPct, decimal? ExpectedMaxHumidityPct,
    int? ShelfLifeDaysSnapshot, DateOnly? ExpectedDeliveryDate,
    DateOnly? ExpiryDate, string? Note);

public sealed record VerifyProductBatchRequest(decimal VerifiedQuantity, decimal VerifiedWeightInKg);

public sealed record VerifyProductBatchResponse(
    long Id, string BatchCode, decimal VerifiedQuantity,
    decimal VerifiedWeightInKg, string BatchStatus);
