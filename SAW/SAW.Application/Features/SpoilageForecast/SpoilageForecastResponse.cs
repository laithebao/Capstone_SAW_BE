namespace SAW.Application.Features.SpoilageForecast;

public sealed record SpoilageForecastResponse(
    long ProductBatchId,
    string BatchCode,
    string ProductName,
    string CropName,
    string? QualityGrade,
    
    // Kết quả dự báo
    decimal SpoilageRiskScore,           // 0 - 100
    decimal? PredictedLossPercent,       // % hao hụt dự kiến
    int? SafeStorageRemainingDays,      // Số ngày lưu kho an toàn còn lại
    string RiskLevel,                    // LOW | MEDIUM | HIGH | CRITICAL
    int? ForecastHorizonDays,            // Số ngày dự báo
    
    // Khuyến nghị & Hành động
    string Recommendation,
    IReadOnlyList<ForecastActionItem> Actions,
    string? AnalysisNotes,
    
    // Metadata
    DateTimeOffset GeneratedAt,
    bool IsFromCache,
    string ModelVersion,
    
    // Xử lý lỗi an toàn
    bool IsError = false,
    string? ErrorMessage = null
);
