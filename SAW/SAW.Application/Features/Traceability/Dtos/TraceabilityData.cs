namespace SAW.Application.Features.Traceability.Dtos;

// Repository projection: contains only public fields plus minimal eligibility evidence.
// This type is never returned directly by a controller.
public sealed class TraceabilityData
{
    public bool IsActive { get; init; }
    public string? QrImageUrl { get; init; }
    public string BatchStatus { get; init; } = "";
    public string? BatchQualityGrade { get; init; }
    public TraceabilityQcEvidence? LatestQc { get; init; }
    public string BatchCode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string CropTypeName { get; init; } = "";
    public string SupplierName { get; init; } = "";
    public PublicTraceabilityOrigin Origin { get; init; } = new("", "", "");
    public DateOnly HarvestDate { get; init; }
    public DateOnly? ExpiryDate { get; init; }
    public IReadOnlyList<DateTime> ReceiptMilestones { get; init; } = [];
    public IReadOnlyList<DateTime> IssueMilestones { get; init; } = [];
}

public sealed record TraceabilityQcEvidence(
    string InspectionStatus, string? Result, string? Grade, DateTime StartedAt, DateTime? CompletedAt)
{
    public PublicTraceabilityStandard? Standard { get; init; }
    public decimal? SamplingRatio { get; init; }
    public decimal? SampleSize { get; init; }
    public IReadOnlyList<TraceabilityCriterionEvidence> Criteria { get; init; } = [];
}

public sealed record TraceabilityCriterionEvidence(string Code, string Name, string Group, string DataType,
    string? Unit, bool IsRequired, TraceabilityCriterionResult? Result, IReadOnlyList<PublicTraceabilityGradeRule> Rules);
public sealed record TraceabilityCriterionResult(decimal? NumericValue, string? TextValue, bool? BooleanValue,
    string? EvaluatedGrade, bool IsPassed);
