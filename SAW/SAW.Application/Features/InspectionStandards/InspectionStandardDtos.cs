namespace SAW.Application.Features.InspectionStandards;
public sealed record SaveInspectionCriterionRequest(string Code, string Name, string CriterionGroup, string DataType, string? Unit, bool IsRequired, bool IsCritical, decimal? MinValue, decimal? MaxValue, string? RequiredTextValue, bool IsFailRule);
public sealed record CreateInspectionStandardRequest(int CropTypeId, string Code, string Name, string? Description, int VersionNo, DateOnly? EffectiveFrom, IReadOnlyList<SaveInspectionCriterionRequest> Criteria);
public sealed record InspectionStandardDto(int Id, string Code, string Name, int CropTypeId, int VersionNo, int CriterionCount);
public sealed record InspectionStandardListItem(int Id, string Code, string Name, string CropTypeName, int VersionNo, string Status, DateOnly? EffectiveFrom, int CriterionCount);
