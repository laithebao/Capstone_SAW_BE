namespace SAW.Domain.Entities;

public class InspectionStandardVersion
{
    public long InspectionStandardVersionId { get; set; }
    public int InspectionStandardSetId { get; set; }

    public int VersionNo { get; set; }
    public string VersionStatus { get; set; } = "DRAFT";

    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation
    public InspectionStandardSet InspectionStandardSet { get; set; } = default!;
    public ICollection<StandardVersionReview> Reviews { get; set; } = [];
    public ICollection<InspectionCriterion> Criteria { get; set; } = [];
    public ICollection<QcInspection> QcInspections { get; set; } = [];
}
