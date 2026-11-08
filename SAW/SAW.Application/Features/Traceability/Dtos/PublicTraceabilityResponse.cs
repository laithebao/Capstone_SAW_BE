namespace SAW.Application.Features.Traceability.Dtos;

public sealed record PublicTraceabilityResponse(
    string BatchCode,
    string ProductName,
    string CropTypeName,
    string SupplierName,
    PublicTraceabilityOrigin Origin,
    DateOnly HarvestDate,
    DateOnly? ExpiryDate,
    PublicTraceabilityQuality Quality,
    IReadOnlyList<PublicTraceabilityMilestone> Milestones);

public sealed record PublicTraceabilityOrigin(string AreaName, string Region, string Province);
public sealed record PublicTraceabilityQuality(string Grade, string Result, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, PublicTraceabilityStandard? Standard, PublicTraceabilitySampling? Sampling,
    IReadOnlyList<PublicTraceabilityCriterion> Criteria, string GradeExplanation, IReadOnlyList<string> DeterminingCriteria);
public sealed record PublicTraceabilityStandard(string Code, string Name, int Version);
public sealed record PublicTraceabilitySampling(decimal RatioPercent, decimal SampleWeightKg);
public sealed record PublicTraceabilityCriterion(string Code, string Name, string GroupLabel, string DataType,
    string? Unit, decimal? NumericValue, string? TextValue, bool? BooleanValue, bool HasResult,
    string? EvaluatedGrade, bool? IsPassed, string AssessmentBasis, IReadOnlyList<PublicTraceabilityGradeRule> Rules);
public sealed record PublicTraceabilityGradeRule(string Grade, decimal? MinValue, decimal? MaxValue,
    string? RequiredTextValue, bool IsFailRule);
public sealed record PublicTraceabilityMilestone(string Type, string Label, DateTimeOffset OccurredAt);
