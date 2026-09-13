namespace SAW.Domain.Entities;

public class InspectionCriterion
{
    public long InspectionCriterionId { get; set; }
    public long InspectionStandardVersionId { get; set; }

    public string CriterionCode { get; set; } = default!;
    public string CriterionName { get; set; } = default!;
    public string CriterionGroup { get; set; } = default!;
    public string DataType { get; set; } = default!;
    public string? Unit { get; set; }

    public bool IsRequired { get; set; }
    public bool IsCritical { get; set; }

    // Navigation
    public InspectionStandardVersion InspectionStandardVersion { get; set; } = default!;
    public ICollection<CriterionGradeRule> GradeRules { get; set; } = [];
    public ICollection<InspectionResultDetail> ResultDetails { get; set; } = [];
}
