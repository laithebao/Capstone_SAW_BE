namespace SAW.Domain.Entities;

public class CriterionGradeRule
{
    public long CriterionGradeRuleId { get; set; }
    public long InspectionCriterionId { get; set; }

    public string Grade { get; set; } = default!;
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string? RequiredTextValue { get; set; }
    public bool IsFailRule { get; set; }

    // Navigation
    public InspectionCriterion InspectionCriterion { get; set; } = default!;
}
