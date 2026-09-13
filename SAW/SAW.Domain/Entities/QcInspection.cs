namespace SAW.Domain.Entities;

public class QcInspection
{
    public long QcInspectionId { get; set; }
    public string InspectionCode { get; set; } = default!;

    public long ProductBatchId { get; set; }
    public long InspectionStandardVersionId { get; set; }
    public int QcAccountId { get; set; }

    public decimal? SamplingRatio { get; set; }
    public decimal? SampleSize { get; set; }

    public string InspectionStatus { get; set; } = "DRAFT";
    public string? QcResult { get; set; }
    public string? QualityGrade { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? Note { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
    public InspectionStandardVersion InspectionStandardVersion { get; set; } = default!;
    public Account QcAccount { get; set; } = default!;
    public SensoryResult? SensoryResult { get; set; }
    public LabResult? LabResult { get; set; }
    public ICollection<InspectionResultDetail> ResultDetails { get; set; } = [];
    public ICollection<QualityImage> QualityImages { get; set; } = [];
    public ICollection<EnvironmentLog> EnvironmentLogs { get; set; } = [];
}
