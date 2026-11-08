namespace SAW.Domain.Entities;

public class SensoryResult
{
    public long SensoryResultId { get; set; }
    public long QcInspectionId { get; set; }

    public decimal? FreshnessScore { get; set; }
    public decimal? SizeScore { get; set; }
    public decimal? ColorScore { get; set; }
    public decimal? RipenessScore { get; set; }
    public decimal? DamagePercentage { get; set; }
    public string? Note { get; set; }

    // Navigation
    public QcInspection QcInspection { get; set; } = default!;
}
