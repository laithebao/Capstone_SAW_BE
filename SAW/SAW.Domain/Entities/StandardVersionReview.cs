namespace SAW.Domain.Entities;

public class StandardVersionReview
{
    public long StandardVersionReviewId { get; set; }
    public long InspectionStandardVersionId { get; set; }
    public int ReviewedByAccountId { get; set; }

    public int ReviewSequence { get; set; }
    public string ReviewDecision { get; set; } = default!;
    public string? Comments { get; set; }
    public DateTime ReviewedAt { get; set; }

    // Navigation
    public InspectionStandardVersion InspectionStandardVersion { get; set; } = default!;
    public Account ReviewedByAccount { get; set; } = default!;
}
