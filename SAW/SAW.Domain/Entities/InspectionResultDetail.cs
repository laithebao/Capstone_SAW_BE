namespace SAW.Domain.Entities;

public class InspectionResultDetail
{
    public long InspectionResultDetailId { get; set; }
    public long QcInspectionId { get; set; }
    public long InspectionCriterionId { get; set; }

    public decimal? NumericValue { get; set; }
    public string? TextValue { get; set; }
    public bool? BooleanValue { get; set; }

    public string? EvaluatedGrade { get; set; }
    public bool IsPassed { get; set; }
    public string? Remarks { get; set; }

    // Navigation
    public QcInspection QcInspection { get; set; } = default!;
    public InspectionCriterion InspectionCriterion { get; set; } = default!;
}
