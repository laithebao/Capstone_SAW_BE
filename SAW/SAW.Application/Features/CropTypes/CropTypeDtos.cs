using System.ComponentModel.DataAnnotations;

namespace SAW.Application.Features.CropTypes;

public sealed record CropTypeDto(
    int Id, string Code, string Name, string CategoryName, string? ScientificName,
    decimal? MinTemperature, decimal? MaxTemperature,
    decimal? MinHumidity, decimal? MaxHumidity,
    int? ShelfLifeDays, decimal? SafetyStockLevelKg,
    string DefaultUnit, bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

public sealed record CropTypeListResponse(
    IReadOnlyList<CropTypeDto> Items, int TotalCount, int Page, int PageSize);

public sealed record SaveCropTypeRequest(
    [Required, MaxLength(30)] string Code,
    [Required, MaxLength(150)] string Name,
    [Required, MaxLength(100)] string CategoryName,
    [MaxLength(1000)] string? ScientificName,
    [Range(-50, 100)] decimal? MinTemperature,
    [Range(-50, 100)] decimal? MaxTemperature,
    [Range(0, 100)] decimal? MinHumidity,
    [Range(0, 100)] decimal? MaxHumidity,
    [Range(1, 3650)] int? ShelfLifeDays,
    [Range(0, double.MaxValue)] decimal? SafetyStockLevelKg,
    [Required, MaxLength(20)] string DefaultUnit,
    bool IsActive = true);

public sealed record SetCropTypeStatusRequest(bool IsActive);
