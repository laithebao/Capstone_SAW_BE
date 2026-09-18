using System.ComponentModel.DataAnnotations;

namespace SAW.Application.Features.InspectionStandards;

// ── Shared criterion request ───────────────────────────────────────────────────
public sealed record SaveInspectionCriterionRequest(
    [Required, MaxLength(100)] string Code,
    [Required, MaxLength(300)] string Name,
    [MaxLength(60)]  string CriterionGroup,
    [Required, MaxLength(40)] string DataType,
    [MaxLength(100)] string? Unit,
    bool IsRequired,
    bool IsCritical,
    decimal? MinValue,
    decimal? MaxValue,
    [MaxLength(200)] string? RequiredTextValue,
    bool IsFailRule);

// ── UC12 – Create Inspection Standard Set ──────────────────────────────────────
public sealed record CreateInspectionStandardRequest(
    int CropTypeId,
    [Required, MaxLength(80)]  string Code,
    [Required, MaxLength(400)] string Name,
    [MaxLength(2000)] string? Description,
    int VersionNo,
    DateOnly? EffectiveFrom,
    IReadOnlyList<SaveInspectionCriterionRequest> Criteria);

// ── UC13 – Create New Version for existing Standard Set ───────────────────────
public sealed record CreateInspectionStandardVersionRequest(
    DateOnly? EffectiveFrom,
    IReadOnlyList<SaveInspectionCriterionRequest> Criteria);

// ── Response DTOs ──────────────────────────────────────────────────────────────
public sealed record InspectionStandardDto(
    int Id, string Code, string Name, int CropTypeId, int VersionNo, int CriterionCount);

public sealed record InspectionStandardListItem(
    int Id, string Code, string Name, string CropTypeName,
    int VersionNo, string Status, DateOnly? EffectiveFrom, int CriterionCount);

public sealed record CriterionDto(
    long Id, string Code, string Name, string CriterionGroup,
    string DataType, string? Unit, bool IsRequired, bool IsCritical,
    decimal? MinValue, decimal? MaxValue, string? RequiredTextValue, bool IsFailRule);

public sealed record InspectionStandardVersionDto(
    long VersionId, int VersionNo, string Status,
    DateOnly? EffectiveFrom, DateOnly? EffectiveTo,
    DateTime CreatedAt, int CriterionCount,
    IReadOnlyList<CriterionDto> Criteria);

public sealed record InspectionStandardDetailDto(
    int Id, string Code, string Name, string? Description,
    int CropTypeId, string CropTypeName, bool IsActive, DateTime CreatedAt,
    IReadOnlyList<InspectionStandardVersionDto> Versions);

public sealed record InspectionStandardVersionCreatedDto(
    int SetId, string SetCode, string SetName,
    long VersionId, int VersionNo, string Status,
    DateOnly? EffectiveFrom, int CriterionCount);
