using System.ComponentModel.DataAnnotations;

namespace SAW.Application.Features.QcInspections;

// ═══════════════════════════════════════════════════════════════════════════════
// UC20 – Create Inspection Form
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record CreateQcInspectionRequest(
    [Required] long ProductBatchId,
    [Required] long InspectionStandardVersionId,
    [MaxLength(1000)] string? Note
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC21 – Declare Batch Sampling Ratio
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record UpdateSamplingRatioRequest(
    /// <summary>Tỷ lệ lấy mẫu (0 < ratio ≤ 1). Ví dụ: 0.10 = 10%</summary>
    [Required] decimal SamplingRatio
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC22 – Input Sensory Inspection Result
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record SaveCriterionResultRequest(
    [Required] long InspectionCriterionId,
    decimal? NumericValue,
    [MaxLength(500)] string? TextValue,
    bool? BooleanValue,
    [MaxLength(500)] string? Remarks
);

public sealed record SaveSensoryResultRequest(
    decimal? FreshnessScore,
    decimal? SizeScore,
    decimal? ColorScore,
    decimal? RipenessScore,
    decimal? DamagePercentage,
    [MaxLength(1000)] string? Note,
    IReadOnlyList<SaveCriterionResultRequest> CriteriaResults
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC23 – Upload Quality Evidence Image
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record UploadQualityImageRequest(
    [Required, MaxLength(255)] string FileName,
    [Required, MaxLength(1000)] string FileUrl,
    [MaxLength(100)] string? MimeType
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC24 – Input Laboratory Test Result
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record SaveLabResultRequest(
    /// <summary>PASS | FAIL | NOT_TESTED</summary>
    [MaxLength(20)] string? ChemicalResidueStatus,
    decimal? ResidueValue,
    [MaxLength(30)] string? ResidueUnit,
    /// <summary>PASS | FAIL | NOT_TESTED</summary>
    [MaxLength(20)] string? PathogenStatus,
    [MaxLength(150)] string? PathogenName,
    [MaxLength(200)] string? LabName,
    DateTime? TestedAt,
    [MaxLength(1000)] string? Note,
    IReadOnlyList<SaveCriterionResultRequest> CriteriaResults
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC22b – Input Environment Criteria Result
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record SaveEnvironmentCriteriaRequest(
    [MaxLength(1000)] string? Note,
    IReadOnlyList<SaveCriterionResultRequest> CriteriaResults
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC25 – Input Actual Storage Temperature
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record CreateEnvironmentLogRequest(
    [Required] long ProductBatchId,
    /// <summary>Không bắt buộc khi đang trong phiên kiểm định (lô chưa nhập kho)</summary>
    int? WarehouseLocationId,
    long? QcInspectionId,
    [Required] decimal TemperatureC,
    decimal? HumidityPct,
    /// <summary>MANUAL | IOT_SENSOR | IMPORT</summary>
    [Required, MaxLength(20)] string SourceType,
    [MaxLength(100)] string? SensorIdentifier,
    [Required] DateTime RecordedAt,
    [MaxLength(1000)] string? Note
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC54 – Reject Batch
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record RejectBatchRequest(
    [Required, MaxLength(1000)] string RejectionReason
);

// ═══════════════════════════════════════════════════════════════════════════════
// UC62 – View Inspection List
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record QcInspectionListQuery(
    string? BatchCode,
    string? InspectionCode,
    string? Status,
    string? QcResult,
    int? QcAccountId,
    DateTime? FromDate,
    DateTime? ToDate,
    int Page = 1,
    int PageSize = 20
);

// ═══════════════════════════════════════════════════════════════════════════════
// Response DTOs
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record QcInspectionDto(
    long Id,
    string InspectionCode,
    long ProductBatchId,
    string BatchCode,
    string ProductName,
    string CropTypeName,
    long InspectionStandardVersionId,
    string StandardCode,
    string StandardName,
    int VersionNo,
    int QcAccountId,
    string QcAccountName,
    decimal? SamplingRatio,
    decimal? SampleSize,
    string InspectionStatus,
    string? QcResult,
    string? QualityGrade,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? Note
);

public sealed record QcInspectionListItem(
    long Id,
    string InspectionCode,
    string BatchCode,
    string ProductName,
    string CropTypeName,
    string QcAccountName,
    string InspectionStatus,
    string? QcResult,
    string? QualityGrade,
    DateTime StartedAt,
    DateTime? CompletedAt
);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize
);

public sealed record GradeRuleSnapshot(
    string Grade,
    decimal? MinValue,
    decimal? MaxValue,
    string? RequiredTextValue,
    bool IsFailRule
);

public sealed record CriterionResultDto(
    long DetailId,
    long CriterionId,
    string CriterionCode,
    string CriterionName,
    string CriterionGroup,
    string DataType,
    string? Unit,
    bool IsRequired,
    bool IsCritical,
    decimal? NumericValue,
    string? TextValue,
    bool? BooleanValue,
    string? EvaluatedGrade,
    bool IsPassed,
    string? Remarks,
    IReadOnlyList<GradeRuleSnapshot> GradeRules
);

public sealed record SensoryResultDto(
    long Id,
    decimal? FreshnessScore,
    decimal? SizeScore,
    decimal? ColorScore,
    decimal? RipenessScore,
    decimal? DamagePercentage,
    string? Note
);

public sealed record LabResultDto(
    long Id,
    string? ChemicalResidueStatus,
    decimal? ResidueValue,
    string? ResidueUnit,
    string? PathogenStatus,
    string? PathogenName,
    string? LabName,
    DateTime? TestedAt,
    string? Note
);

public sealed record QualityImageDto(
    long Id,
    string FileName,
    string FileUrl,
    string? MimeType,
    int UploadedByAccountId,
    string UploadedByName,
    DateTime UploadedAt
);

public sealed record EnvironmentLogDto(
    long Id,
    long ProductBatchId,
    int? WarehouseLocationId,    // null khi ghi trong phiên kiểm định (lô chưa nhập kho)
    string LocationCode,
    decimal TemperatureC,
    decimal? HumidityPct,
    string SourceType,
    string? SensorIdentifier,
    DateTime RecordedAt,
    string? Note,
    bool? IsTempOutOfRange
);

public sealed record QcInspectionDetailDto(
    long Id,
    string InspectionCode,
    long ProductBatchId,
    string BatchCode,
    string ProductName,
    string CropTypeName,
    decimal? ExpectedMinTempC,
    decimal? ExpectedMaxTempC,
    decimal? ExpectedMinHumidityPct,
    decimal? ExpectedMaxHumidityPct,
    long InspectionStandardVersionId,
    string StandardCode,
    string StandardName,
    int VersionNo,
    int QcAccountId,
    string QcAccountName,
    decimal? SamplingRatio,
    decimal? SampleSize,
    string InspectionStatus,
    string? QcResult,
    string? QualityGrade,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? Note,
    SensoryResultDto? SensoryResult,
    LabResultDto? LabResult,
    IReadOnlyList<QualityImageDto> Images,
    IReadOnlyList<CriterionResultDto> CriteriaResults,
    IReadOnlyList<EnvironmentLogDto> EnvironmentLogs
);

public sealed record FinalizeQcResultDto(
    long InspectionId,
    string InspectionCode,
    string QcResult,       // PASS | FAIL
    string? QualityGrade,  // A-E (null nếu FAIL trước khi grade được xác định)
    string NewBatchStatus,
    string Summary         // Mô tả kết quả
);
