namespace SAW.Domain.Entities;

public class AiRiskForecast
{
    public long AiRiskForecastId { get; set; }

    public long ProductBatchId { get; set; }

    public decimal SpoilageRiskScore { get; set; }
    public decimal? PredictedLossPercent { get; set; }
    public int? SafeStorageRemainingDays { get; set; }

    public string RiskLevel { get; set; } = default!;
    public int? ForecastHorizonDays { get; set; }

    public string? Recommendation { get; set; }
    public string? ModelVersion { get; set; }
    public string? InputSnapshotJson { get; set; }

    public DateTime GeneratedAt { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
}
