using SAW.Application.Exceptions;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Application.Features.QcInspections;

/// <summary>
/// Business logic cho tất cả Use Cases kiểm định chất lượng QC:
///   UC20 – Create Inspection Form
///   UC21 – Declare Batch Sampling Ratio
///   UC22 – Input Sensory Inspection Result
///   UC23 – Upload Quality Evidence Image
///   UC24 – Input Laboratory Test Result
///   UC25 – Input Actual Storage Temperature
///   UC52 – Compare Inspection Data with Rule Set
///   UC53 – Classify Product Quality Grade
///   UC54 – Reject Batch with Serious Defect
///   UC62 – View Inspection List
///
/// BUSINESS RULE QUAN TRỌNG (BOOLEAN criteria):
///   - BOOLEAN criteria là cổng Pass/Fail TUYỆT ĐỐI (VD: Aflatoxin, thuốc trừ sâu cấm).
///   - BOOLEAN criteria KHÔNG tham gia xếp hạng A-E.
///   - BooleanValue = false → IsPassed = false → nếu IsCritical = true → FAIL ngay lập tức.
///   - Chỉ NUMBER và TEXT criteria tham gia tính OverallGrade.
/// </summary>
public sealed class QcInspectionService(IQcInspectionRepository repository) : IQcInspectionService
{
    // ─── Constants ────────────────────────────────────────────────────────────
    private static readonly string[] GradeOrder = ["A", "B", "C", "D", "E"];

    private static readonly HashSet<string> ValidStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "PASS", "FAIL", "NOT_TESTED" };

    private static readonly HashSet<string> ValidSourceTypes =
        new(StringComparer.OrdinalIgnoreCase) { "MANUAL", "IOT_SENSOR", "IMPORT" };

    private static readonly HashSet<string> ValidMimeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    private const int MaxImagesPerInspection = 20;

    // ═══════════════════════════════════════════════════════════════════════════
    // UC20 – Create Inspection Form
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<QcInspectionDto> CreateAsync(
        CreateQcInspectionRequest request,
        int actorAccountId,
        CancellationToken ct)
    {
        // Validate batch
        var batch = await repository.GetBatchAsync(request.ProductBatchId, ct)
            ?? throw new NotFoundException($"Không tìm thấy lô hàng với ID {request.ProductBatchId}.");

        if (batch.BatchStatus != "PENDING_QC")
            throw new BadRequestException(
                $"Lô hàng '{batch.BatchCode}' không ở trạng thái chờ kiểm định (hiện tại: {batch.BatchStatus}). " +
                "Chỉ kiểm định lô có trạng thái PENDING_QC.");

        // Validate version
        var version = await repository.GetVersionWithCriteriaAsync(request.InspectionStandardVersionId, ct)
            ?? throw new NotFoundException(
                $"Không tìm thấy phiên bản tiêu chuẩn ID {request.InspectionStandardVersionId}.");

        if (version.VersionStatus != "PUBLISHED")
            throw new BadRequestException(
                $"Phiên bản tiêu chuẩn v{version.VersionNo} chưa được phê duyệt (trạng thái: {version.VersionStatus}). " +
                "Chỉ áp dụng phiên bản PUBLISHED.");

        if (version.InspectionStandardSet.CropTypeId != batch.CropTypeId)
            throw new BadRequestException(
                "Phiên bản tiêu chuẩn không phù hợp với loại nông sản của lô hàng.");

        // Check duplicate active inspection
        if (await repository.HasActiveInspectionAsync(request.ProductBatchId, ct))
            throw new ConflictException(
                $"Lô hàng '{batch.BatchCode}' đã có phiếu kiểm định đang thực hiện. " +
                "Không thể tạo phiếu mới khi chưa hoàn thành phiếu cũ.");

        // Generate InspectionCode: QC-{BatchCode}-{YYYYMMDD}-{random4}
        var code = await GenerateUniqueCodeAsync(batch.BatchCode, ct);

        // Build inspection
        var inspection = new QcInspection
        {
            InspectionCode              = code,
            ProductBatchId              = request.ProductBatchId,
            InspectionStandardVersionId = request.InspectionStandardVersionId,
            QcAccountId                 = actorAccountId,
            InspectionStatus            = "DRAFT",
            StartedAt                   = DateTime.UtcNow,
            Note                        = string.IsNullOrWhiteSpace(request.Note)
                                            ? null : request.Note.Trim()
        };

        // Pre-populate INSPECTION_RESULT_DETAIL placeholders for each criterion
        foreach (var criterion in version.Criteria)
        {
            inspection.ResultDetails.Add(new InspectionResultDetail
            {
                InspectionCriterionId = criterion.InspectionCriterionId,
                IsPassed              = true // default, will be updated during finalize
            });
        }

        repository.AddInspection(inspection);
        await repository.SaveChangesAsync(ct);

        return MapToDto(inspection, batch, version);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC21 – Declare Batch Sampling Ratio
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task UpdateSamplingRatioAsync(
        long inspectionId,
        UpdateSamplingRatioRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await GetEditableInspectionAsync(inspectionId, actorAccountId, actorRole, ct);

        // Validate sampling ratio
        if (request.SamplingRatio <= 0 || request.SamplingRatio > 1)
            throw new BadRequestException(
                "Tỷ lệ lấy mẫu phải trong khoảng (0, 1]. Ví dụ: 0.10 = 10%.");

        // Business rule: minimum ratio
        var batch = inspection.ProductBatch;
        decimal minRatio = batch.WeightInKg < 1000m ? 0.05m : 0.03m;
        if (request.SamplingRatio < minRatio)
            throw new BadRequestException(
                $"Tỷ lệ lấy mẫu tối thiểu cho lô {batch.WeightInKg:F0} kg là {minRatio * 100:F0}%. " +
                $"Giá trị nhập ({request.SamplingRatio * 100:F2}%) quá thấp.");

        inspection.SamplingRatio = request.SamplingRatio;
        inspection.SampleSize    = batch.WeightInKg * request.SamplingRatio;

        if (inspection.InspectionStatus == "DRAFT")
            inspection.InspectionStatus = "IN_PROGRESS";

        await repository.SaveChangesAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC22 – Input Sensory Inspection Result
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task SaveSensoryResultAsync(
        long inspectionId,
        SaveSensoryResultRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await GetEditableInspectionAsync(inspectionId, actorAccountId, actorRole, ct);
        EnsureInProgress(inspection);

        // Validate scores
        ValidateScore(request.FreshnessScore, "Độ tươi");
        ValidateScore(request.SizeScore,      "Kích cỡ");
        ValidateScore(request.ColorScore,     "Màu sắc");
        ValidateScore(request.RipenessScore,  "Độ chín");

        if (request.DamagePercentage.HasValue &&
            (request.DamagePercentage < 0 || request.DamagePercentage > 100))
            throw new BadRequestException("Tỷ lệ hư hỏng phải trong khoảng [0, 100]%.");

        // Apply criterion results for SENSORY group
        if (request.CriteriaResults?.Count > 0)
            ApplyCriterionResults(inspection, request.CriteriaResults, "SENSORY");

        // Upsert SENSORY_RESULT
        if (inspection.SensoryResult is null)
        {
            var sensory = new SensoryResult
            {
                QcInspectionId   = inspectionId,
                FreshnessScore   = request.FreshnessScore,
                SizeScore        = request.SizeScore,
                ColorScore       = request.ColorScore,
                RipenessScore    = request.RipenessScore,
                DamagePercentage = request.DamagePercentage,
                Note             = request.Note?.Trim()
            };
            repository.AddSensoryResult(sensory);
        }
        else
        {
            var s = inspection.SensoryResult;
            s.FreshnessScore   = request.FreshnessScore;
            s.SizeScore        = request.SizeScore;
            s.ColorScore       = request.ColorScore;
            s.RipenessScore    = request.RipenessScore;
            s.DamagePercentage = request.DamagePercentage;
            s.Note             = request.Note?.Trim();
        }

        await repository.SaveChangesAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC23 – Upload Quality Evidence Image
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<QualityImageDto> AddImageAsync(
        long inspectionId,
        UploadQualityImageRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await GetEditableInspectionAsync(inspectionId, actorAccountId, actorRole, ct);

        // Validate MIME type
        if (!string.IsNullOrWhiteSpace(request.MimeType) &&
            !ValidMimeTypes.Contains(request.MimeType))
            throw new BadRequestException(
                $"Loại file '{request.MimeType}' không được hỗ trợ. " +
                "Chỉ chấp nhận: image/jpeg, image/png, image/webp.");

        // Validate Cloudinary URL
        if (!request.FileUrl.Contains("cloudinary.com", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("URL ảnh phải là URL hợp lệ từ Cloudinary.");

        // Validate max images
        var currentCount = await repository.CountImagesAsync(inspectionId, ct);
        if (currentCount >= MaxImagesPerInspection)
            throw new BadRequestException(
                $"Phiếu kiểm định đã đạt giới hạn tối đa {MaxImagesPerInspection} ảnh.");

        var image = new QualityImage
        {
            QcInspectionId       = inspectionId,
            FileName             = request.FileName.Trim(),
            FileUrl              = request.FileUrl.Trim(),
            MimeType             = request.MimeType?.Trim(),
            UploadedByAccountId  = actorAccountId,
            UploadedAt           = DateTime.UtcNow
        };

        repository.AddImage(image);
        await repository.SaveChangesAsync(ct);

        return new QualityImageDto(
            image.QualityImageId,
            image.FileName,
            image.FileUrl,
            image.MimeType,
            image.UploadedByAccountId,
            inspection.QcAccount?.FullName ?? string.Empty,
            image.UploadedAt);
    }

    public async Task DeleteImageAsync(
        long inspectionId,
        long imageId,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await GetEditableInspectionAsync(inspectionId, actorAccountId, actorRole, ct);

        var image = await repository.GetImageAsync(imageId, ct)
            ?? throw new NotFoundException($"Không tìm thấy ảnh ID {imageId}.");

        if (image.QcInspectionId != inspectionId)
            throw new BadRequestException("Ảnh không thuộc phiếu kiểm định này.");

        repository.RemoveImage(image);
        await repository.SaveChangesAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC24 – Input Laboratory Test Result
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task SaveLabResultAsync(
        long inspectionId,
        SaveLabResultRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await GetEditableInspectionAsync(inspectionId, actorAccountId, actorRole, ct);
        EnsureInProgress(inspection);

        // Validate status enums
        if (!string.IsNullOrWhiteSpace(request.ChemicalResidueStatus) &&
            !ValidStatuses.Contains(request.ChemicalResidueStatus))
            throw new BadRequestException(
                $"Trạng thái dư lượng hóa học '{request.ChemicalResidueStatus}' không hợp lệ. " +
                "Cho phép: PASS, FAIL, NOT_TESTED.");

        if (!string.IsNullOrWhiteSpace(request.PathogenStatus) &&
            !ValidStatuses.Contains(request.PathogenStatus))
            throw new BadRequestException(
                $"Trạng thái vi sinh '{request.PathogenStatus}' không hợp lệ. " +
                "Cho phép: PASS, FAIL, NOT_TESTED.");

        // Conditional required
        if (request.ChemicalResidueStatus?.Equals("FAIL", StringComparison.OrdinalIgnoreCase) == true &&
            !request.ResidueValue.HasValue)
            throw new BadRequestException(
                "Trạng thái dư lượng hóa học là FAIL — bắt buộc nhập giá trị dư lượng (ResidueValue).");

        if (request.PathogenStatus?.Equals("FAIL", StringComparison.OrdinalIgnoreCase) == true &&
            string.IsNullOrWhiteSpace(request.PathogenName))
            throw new BadRequestException(
                "Trạng thái vi sinh là FAIL — bắt buộc nhập tên vi sinh vật (PathogenName).");

        if (request.ResidueValue.HasValue && request.ResidueValue < 0)
            throw new BadRequestException("Giá trị dư lượng không được âm.");

        if (request.TestedAt.HasValue && request.TestedAt.Value > DateTime.UtcNow)
            throw new BadRequestException("Ngày kiểm nghiệm không được là ngày trong tương lai.");

        // Apply criterion results for LAB group
        if (request.CriteriaResults?.Count > 0)
            ApplyCriterionResults(inspection, request.CriteriaResults, "LAB");

        // Upsert LAB_RESULT
        if (inspection.LabResult is null)
        {
            var lab = new LabResult
            {
                QcInspectionId        = inspectionId,
                ChemicalResidueStatus = request.ChemicalResidueStatus?.ToUpperInvariant(),
                ResidueValue          = request.ResidueValue,
                ResidueUnit           = request.ResidueUnit?.Trim(),
                PathogenStatus        = request.PathogenStatus?.ToUpperInvariant(),
                PathogenName          = request.PathogenName?.Trim(),
                LabName               = request.LabName?.Trim(),
                TestedAt              = request.TestedAt,
                Note                  = request.Note?.Trim()
            };
            repository.AddLabResult(lab);
        }
        else
        {
            var l = inspection.LabResult;
            l.ChemicalResidueStatus = request.ChemicalResidueStatus?.ToUpperInvariant();
            l.ResidueValue          = request.ResidueValue;
            l.ResidueUnit           = request.ResidueUnit?.Trim();
            l.PathogenStatus        = request.PathogenStatus?.ToUpperInvariant();
            l.PathogenName          = request.PathogenName?.Trim();
            l.LabName               = request.LabName?.Trim();
            l.TestedAt              = request.TestedAt;
            l.Note                  = request.Note?.Trim();
        }

        await repository.SaveChangesAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC22b – Input Environment Criteria Result (ENVIRONMENT group)
    // Tương tự UC22/UC24 nhưng lưu tiêu chí nhóm ENVIRONMENT
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task SaveEnvironmentCriteriaAsync(
        long inspectionId,
        SaveEnvironmentCriteriaRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await GetEditableInspectionAsync(inspectionId, actorAccountId, actorRole, ct);
        EnsureInProgress(inspection);

        if (request.CriteriaResults?.Count > 0)
            ApplyCriterionResults(inspection, request.CriteriaResults, "ENVIRONMENT");

        await repository.SaveChangesAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC25 – Input Actual Storage Temperature
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<EnvironmentLogDto> CreateEnvironmentLogAsync(
        CreateEnvironmentLogRequest request,
        int actorAccountId,
        CancellationToken ct)
    {
        // Validate
        if (!ValidSourceTypes.Contains(request.SourceType))
            throw new BadRequestException(
                $"SourceType '{request.SourceType}' không hợp lệ. Cho phép: MANUAL, IOT_SENSOR, IMPORT.");

        if (request.HumidityPct.HasValue &&
            (request.HumidityPct < 0 || request.HumidityPct > 100))
            throw new BadRequestException("Độ ẩm phải trong khoảng [0, 100]%.");

        if (request.RecordedAt > DateTime.UtcNow.AddMinutes(5))
            throw new BadRequestException("Thời điểm đo không được là thời gian trong tương lai.");

        // Validate location — chỉ bắt buộc khi lô đã nhập kho (không trong phiên QC)
        if (request.WarehouseLocationId.HasValue &&
            !await repository.WarehouseLocationExistsAsync(request.WarehouseLocationId.Value, ct))
            throw new NotFoundException(
                $"Không tìm thấy vị trí kho ID {request.WarehouseLocationId} hoặc vị trí đã ngừng hoạt động.");

        // Validate batch
        var batch = await repository.GetBatchAsync(request.ProductBatchId, ct)
            ?? throw new NotFoundException($"Không tìm thấy lô hàng ID {request.ProductBatchId}.");

        var log = new EnvironmentLog
        {
            ProductBatchId       = request.ProductBatchId,
            WarehouseLocationId  = request.WarehouseLocationId,
            QcInspectionId       = request.QcInspectionId,
            RecordedByAccountId  = actorAccountId,
            TemperatureC         = request.TemperatureC,
            HumidityPct          = request.HumidityPct,
            SourceType           = request.SourceType.ToUpperInvariant(),
            SensorIdentifier     = request.SensorIdentifier?.Trim(),
            RecordedAt           = request.RecordedAt,
            Note                 = request.Note?.Trim()
        };

        repository.AddEnvironmentLog(log);
        await repository.SaveChangesAsync(ct);

        // Check temperature out of range for alert
        bool? isTempOutOfRange = null;
        if (batch.ExpectedMinTempC.HasValue || batch.ExpectedMaxTempC.HasValue)
        {
            isTempOutOfRange =
                (batch.ExpectedMinTempC.HasValue && log.TemperatureC < batch.ExpectedMinTempC.Value) ||
                (batch.ExpectedMaxTempC.HasValue && log.TemperatureC > batch.ExpectedMaxTempC.Value);
        }

        return new EnvironmentLogDto(
            log.EnvironmentLogId,
            log.ProductBatchId,
            log.WarehouseLocationId,
            log.WarehouseLocation?.LocationCode ?? string.Empty,
            log.TemperatureC,
            log.HumidityPct,
            log.SourceType,
            log.SensorIdentifier,
            log.RecordedAt,
            log.Note,
            isTempOutOfRange);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC52 + UC53 – Compare with Rule Set & Classify Grade (Finalize)
    //
    // BOOLEAN criteria rules:
    //   - BOOLEAN criteria do NOT participate in grade classification (A-E)
    //   - BOOLEAN = false → IsPassed = false → if IsCritical → FAIL immediately
    //   - Only NUMBER and TEXT criteria contribute to OverallGrade
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<FinalizeQcResultDto> FinalizeAsync(
        long inspectionId,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await repository.GetInspectionDetailAsync(inspectionId, ct)
            ?? throw new NotFoundException($"Không tìm thấy phiếu kiểm định ID {inspectionId}.");

        EnsureOwnership(inspection, actorAccountId, actorRole);

        if (inspection.InspectionStatus == "COMPLETED")
            throw new BadRequestException(
                "Phiếu kiểm định đã hoàn thành và bị khóa bất biến. Không thể đánh giá lại.");

        if (inspection.InspectionStatus == "DRAFT")
            throw new BadRequestException(
                "Phiếu kiểm định chưa được bắt đầu. Vui lòng khai báo tỷ lệ lấy mẫu trước.");

        var version  = inspection.InspectionStandardVersion;
        var criteria = version.Criteria.ToDictionary(c => c.InspectionCriterionId);

        // Validate: all REQUIRED criteria must have value
        foreach (var detail in inspection.ResultDetails)
        {
            if (!criteria.TryGetValue(detail.InspectionCriterionId, out var criterion)) continue;
            if (!criterion.IsRequired) continue;

            var hasValue = criterion.DataType switch
            {
                "NUMBER"  => detail.NumericValue.HasValue,
                "TEXT"    => !string.IsNullOrWhiteSpace(detail.TextValue),
                "BOOLEAN" => detail.BooleanValue.HasValue,
                _         => false
            };

            if (!hasValue)
                throw new BadRequestException(
                    $"Tiêu chí bắt buộc '{criterion.CriterionCode} – {criterion.CriterionName}' " +
                    "chưa được nhập kết quả. Vui lòng nhập đầy đủ trước khi hoàn thành phiếu.");
        }

        // Run evaluation engine
        var (overallGrade, qcResult, failReason) =
            RunGradeEngine(inspection.ResultDetails, criteria);

        // Update inspection
        inspection.QcResult          = qcResult;
        inspection.QualityGrade      = overallGrade;
        inspection.InspectionStatus  = "COMPLETED";
        inspection.CompletedAt       = DateTime.UtcNow;

        // Update batch status and grade
        var batch = inspection.ProductBatch;
        batch.QualityGrade  = overallGrade;
        batch.UpdatedAt     = DateTime.UtcNow;

        string newBatchStatus;
        if (qcResult == "FAIL")
        {
            newBatchStatus        = "REJECTED";
            batch.BatchStatus     = "REJECTED";
            batch.RejectionReason = failReason
                ?? "Không đạt tiêu chí kiểm định chất lượng.";
        }
        else
        {
            // PASS — all grades (A–E) go to APPROVED_FOR_STORAGE
            newBatchStatus    = "APPROVED_FOR_STORAGE";
            batch.BatchStatus = newBatchStatus;
        }

        await repository.SaveChangesAsync(ct);

        var summary = qcResult == "PASS"
            ? $"Lô hàng đạt chất lượng hạng {overallGrade}. Trạng thái: {newBatchStatus}."
            : $"Lô hàng KHÔNG ĐẠT. Lý do: {failReason}";

        return new FinalizeQcResultDto(
            inspection.QcInspectionId,
            inspection.InspectionCode,
            qcResult,
            overallGrade,
            newBatchStatus,
            summary);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC54 – Reject Batch with Serious Defect (Manual override)
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task RejectBatchAsync(
        long inspectionId,
        RejectBatchRequest request,
        int actorAccountId,
        CancellationToken ct)
    {
        var inspection = await repository.GetInspectionDetailAsync(inspectionId, ct)
            ?? throw new NotFoundException($"Không tìm thấy phiếu kiểm định ID {inspectionId}.");

        var batch = inspection.ProductBatch;

        var rejectableStatuses = new HashSet<string>
            { "PENDING_QC", "QUARANTINE", "APPROVED_FOR_STORAGE" };

        if (!rejectableStatuses.Contains(batch.BatchStatus))
            throw new BadRequestException(
                $"Không thể từ chối lô hàng '{batch.BatchCode}' ở trạng thái '{batch.BatchStatus}'. " +
                "Chỉ có thể từ chối lô ở trạng thái: PENDING_QC, QUARANTINE, APPROVED_FOR_STORAGE.");

        batch.BatchStatus     = "REJECTED";
        batch.RejectionReason = request.RejectionReason.Trim();
        batch.UpdatedAt       = DateTime.UtcNow;

        // If inspection is still open, complete it as FAIL
        if (inspection.InspectionStatus != "COMPLETED")
        {
            inspection.QcResult         = "FAIL";
            inspection.InspectionStatus = "COMPLETED";
            inspection.CompletedAt      = DateTime.UtcNow;
        }

        await repository.SaveChangesAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UC62 – View Inspection List
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<PagedResult<QcInspectionListItem>> ListAsync(
        QcInspectionListQuery query,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        // BR-62-01: QC_STAFF only sees own inspections; WAREHOUSE_MANAGER & ADMINISTRATOR see all
        int? filterByAccountId = actorRole.Equals("QC_STAFF", StringComparison.OrdinalIgnoreCase)
            ? actorAccountId
            : query.QcAccountId;

        var (items, total) = await repository.ListAsync(
            query.BatchCode?.Trim(),
            query.InspectionCode?.Trim(),
            query.Status?.Trim(),
            query.QcResult?.Trim(),
            filterByAccountId,
            query.FromDate,
            query.ToDate,
            query.Page,
            Math.Min(query.PageSize, 50), // cap at 50
            ct);

        var dtos = items.Select(i => new QcInspectionListItem(
            i.QcInspectionId,
            i.InspectionCode,
            i.ProductBatch?.BatchCode ?? string.Empty,
            i.ProductBatch?.ProductName ?? string.Empty,
            i.ProductBatch?.CropType?.CropName ?? string.Empty,
            i.QcAccount?.FullName ?? string.Empty,
            i.InspectionStatus,
            i.QcResult,
            i.QualityGrade,
            i.StartedAt,
            i.CompletedAt
        )).ToList();

        return new PagedResult<QcInspectionListItem>(dtos, total, query.Page, query.PageSize);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Detail view
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<QcInspectionDetailDto> GetByIdAsync(
        long inspectionId,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await repository.GetInspectionDetailAsync(inspectionId, ct)
            ?? throw new NotFoundException($"Không tìm thấy phiếu kiểm định ID {inspectionId}.");

        EnsureOwnership(inspection, actorAccountId, actorRole);
        return MapToDetailDto(inspection);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GRADE ENGINE — UC52 + UC53
    //
    // BOOLEAN: Pass/Fail gate only, NOT graded
    // NUMBER & TEXT: participate in A-E grading
    // ═══════════════════════════════════════════════════════════════════════════

    private static (string? overallGrade, string qcResult, string? failReason)
        RunGradeEngine(
            ICollection<InspectionResultDetail> details,
            Dictionary<long, InspectionCriterion> criteria)
    {
        // Phase 1: Evaluate each criterion
        foreach (var detail in details)
        {
            if (!criteria.TryGetValue(detail.InspectionCriterionId, out var criterion))
                continue;

            switch (criterion.DataType.ToUpperInvariant())
            {
                case "BOOLEAN":
                    EvaluateBoolean(detail, criterion);
                    break;
                case "NUMBER":
                    EvaluateNumber(detail, criterion);
                    break;
                case "TEXT":
                    EvaluateText(detail, criterion);
                    break;
            }
        }

        // Phase 2: Check BOOLEAN critical failures (immediate FAIL, no grade)
        foreach (var detail in details)
        {
            if (!criteria.TryGetValue(detail.InspectionCriterionId, out var criterion)) continue;
            if (criterion.DataType.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)
                && !detail.IsPassed
                && criterion.IsCritical)
            {
                return (null, "FAIL",
                    $"Tiêu chí bắt buộc '{criterion.CriterionCode} – {criterion.CriterionName}' " +
                    "không đạt (BOOLEAN: Không đạt/Not Detected Failed).");
            }
        }

        // Phase 3: Check NUMBER/TEXT critical failures
        foreach (var detail in details)
        {
            if (!criteria.TryGetValue(detail.InspectionCriterionId, out var criterion)) continue;
            if (criterion.DataType.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)) continue;
            if (criterion.IsCritical && !detail.IsPassed)
            {
                return (null, "FAIL",
                    $"Tiêu chí nghiêm trọng '{criterion.CriterionCode} – {criterion.CriterionName}' " +
                    $"không đạt ngưỡng cho phép (hạng: {detail.EvaluatedGrade ?? "không xác định"}).");
            }
        }

        // Phase 4: Calculate OverallGrade from NUMBER and TEXT only
        // BOOLEAN criteria are excluded from grading
        var gradedDetails = details
            .Where(d =>
            {
                if (!criteria.TryGetValue(d.InspectionCriterionId, out var c)) return false;
                return !c.DataType.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)
                    && d.EvaluatedGrade != null;
            })
            .ToList();

        if (gradedDetails.Count == 0)
        {
            // No gradable criteria → default to A if all passed
            return ("A", "PASS", null);
        }

        // Worst grade wins
        var worstGrade = GradeOrder
            .LastOrDefault(g => gradedDetails.Any(d => d.EvaluatedGrade == g));

        return (worstGrade ?? "A", "PASS", null);
    }

    private static void EvaluateBoolean(
        InspectionResultDetail detail,
        InspectionCriterion criterion)
    {
        // BOOLEAN: does NOT get a grade, only Pass/Fail
        detail.EvaluatedGrade = null;
        detail.IsPassed       = detail.BooleanValue == true;
        // Note: IsCritical check is done in Phase 2 of RunGradeEngine
    }

    private static void EvaluateNumber(
        InspectionResultDetail detail,
        InspectionCriterion criterion)
    {
        if (!detail.NumericValue.HasValue)
        {
            detail.EvaluatedGrade = null;
            detail.IsPassed       = !criterion.IsRequired;
            return;
        }

        var value = detail.NumericValue.Value;
        CriterionGradeRule? matchedRule = null;

        foreach (var g in GradeOrder)
        {
            var rule = criterion.GradeRules
                .FirstOrDefault(r => r.Grade.Equals(g, StringComparison.OrdinalIgnoreCase));
            if (rule is null) continue;

            var effectiveMin = rule.MinValue ?? decimal.MinValue;
            var effectiveMax = rule.MaxValue ?? decimal.MaxValue;

            if (value >= effectiveMin && value <= effectiveMax)
            {
                matchedRule = rule;
                break;
            }
        }

        if (matchedRule is null)
        {
            // ── Phương án 1: Khoảng trắng → xếp hạng tệ hơn liền kề ──────────
            // Tìm hạng tốt nhất mà giá trị đã VƯỢT QUA max (tức là value > max của hạng đó)
            // → hạng liền kề tệ hơn là hạng tiếp theo trong GradeOrder
            CriterionGradeRule? fallbackRule = null;
            foreach (var g in GradeOrder)
            {
                var rule = criterion.GradeRules
                    .FirstOrDefault(r => r.Grade.Equals(g, StringComparison.OrdinalIgnoreCase));
                if (rule is null) continue;

                var effectiveMax = rule.MaxValue ?? decimal.MaxValue;
                // Giá trị vượt qua max của hạng này → hạng này là "hàng xóm tốt hơn"
                // → hạng hiện tại (g) là hạng tệ hơn liền kề nếu giá trị chưa tới min
                var effectiveMin = rule.MinValue ?? decimal.MinValue;

                if (value < effectiveMin)
                {
                    // Giá trị nhỏ hơn min của hạng g → nằm trong khoảng trắng phía trước hạng g
                    fallbackRule = rule;
                    break;
                }
            }

            detail.EvaluatedGrade = fallbackRule?.Grade;
            detail.IsPassed       = fallbackRule != null && !fallbackRule.IsFailRule;

            if (fallbackRule is not null)
                detail.Remarks =
                    $"Giá trị {value} không thuộc hạng nào — tự động xếp vào Hạng {fallbackRule.Grade} (hạng liền kề tệ hơn do khoảng trắng trong tiêu chuẩn).";
            else
                detail.Remarks =
                    $"Giá trị {value} vượt ngoài toàn bộ khoảng được định nghĩa trong tiêu chuẩn — không thể xếp hạng.";

            return;
        }

        detail.EvaluatedGrade = matchedRule.Grade;
        detail.IsPassed       = !matchedRule.IsFailRule;
    }

    private static void EvaluateText(
        InspectionResultDetail detail,
        InspectionCriterion criterion)
    {
        if (string.IsNullOrWhiteSpace(detail.TextValue))
        {
            detail.EvaluatedGrade = null;
            detail.IsPassed       = !criterion.IsRequired;
            return;
        }

        var matchedRule = criterion.GradeRules.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.RequiredTextValue) &&
            r.RequiredTextValue.Equals(detail.TextValue.Trim(), StringComparison.OrdinalIgnoreCase));

        detail.EvaluatedGrade = matchedRule?.Grade;
        detail.IsPassed       = matchedRule != null && !matchedRule.IsFailRule;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies a list of criterion results to INSPECTION_RESULT_DETAIL.
    /// Only applies to criteria belonging to the specified group (if provided).
    /// </summary>
    private static void ApplyCriterionResults(
        QcInspection inspection,
        IReadOnlyList<SaveCriterionResultRequest> criteriaResults,
        string? expectedGroup = null)
    {
        foreach (var cr in criteriaResults)
        {
            var detail = inspection.ResultDetails
                .FirstOrDefault(d => d.InspectionCriterionId == cr.InspectionCriterionId);

            if (detail is null) continue;

            // Optional group validation
            if (!string.IsNullOrWhiteSpace(expectedGroup))
            {
                var criterion = detail.InspectionCriterion;
                if (criterion is not null &&
                    !criterion.CriterionGroup.Equals(expectedGroup, StringComparison.OrdinalIgnoreCase))
                    continue; // skip mismatched group
            }

            detail.NumericValue = cr.NumericValue;
            detail.TextValue    = string.IsNullOrWhiteSpace(cr.TextValue) ? null : cr.TextValue.Trim();
            detail.BooleanValue = cr.BooleanValue;
            detail.Remarks      = string.IsNullOrWhiteSpace(cr.Remarks) ? null : cr.Remarks.Trim();
        }
    }

    private async Task<QcInspection> GetEditableInspectionAsync(
        long inspectionId,
        int actorAccountId,
        string actorRole,
        CancellationToken ct)
    {
        var inspection = await repository.GetInspectionAsync(inspectionId, ct)
            ?? throw new NotFoundException($"Không tìm thấy phiếu kiểm định ID {inspectionId}.");

        if (inspection.InspectionStatus == "COMPLETED")
            throw new BadRequestException(
                "Phiếu kiểm định đã hoàn thành và bị khóa bất biến. Không thể chỉnh sửa.");

        EnsureOwnership(inspection, actorAccountId, actorRole);
        return inspection;
    }

    /// <summary>
    /// QC_STAFF chỉ được phép truy cập và chỉnh sửa phiếu kiểm định do chính họ tạo.
    /// WAREHOUSE_MANAGER và ADMINISTRATOR được truy cập toàn bộ.
    /// </summary>
    private static void EnsureOwnership(QcInspection inspection, int actorAccountId, string actorRole)
    {
        if (actorRole.Equals("QC_STAFF", StringComparison.OrdinalIgnoreCase)
            && inspection.QcAccountId != actorAccountId)
            throw new ForbiddenException(
                "Bạn không có quyền truy cập phiếu kiểm định này. " +
                "QC_STAFF chỉ được xem và chỉnh sửa phiếu kiểm định do chính mình tạo.");
    }

    private static void EnsureInProgress(QcInspection inspection)
    {
        if (inspection.InspectionStatus == "DRAFT")
            throw new BadRequestException(
                "Phiếu kiểm định chưa được bắt đầu. Vui lòng khai báo tỷ lệ lấy mẫu (UC21) trước.");
    }

    private static void ValidateScore(decimal? score, string fieldName)
    {
        if (score.HasValue && (score < 0 || score > 100))
            throw new BadRequestException($"Điểm '{fieldName}' phải trong khoảng [0, 100].");
    }

    private async Task<string> GenerateUniqueCodeAsync(string batchCode, CancellationToken ct)
    {
        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        var rnd  = Random.Shared.Next(1000, 9999);
        var code = $"QC-{batchCode}-{date}-{rnd}";

        // Retry if collision (very rare)
        var attempts = 0;
        while (await repository.InspectionCodeExistsAsync(code, ct) && attempts < 5)
        {
            rnd  = Random.Shared.Next(1000, 9999);
            code = $"QC-{batchCode}-{date}-{rnd}";
            attempts++;
        }

        return code;
    }

    // ─── Mapping ──────────────────────────────────────────────────────────────

    private static QcInspectionDto MapToDto(
        QcInspection i,
        ProductBatch batch,
        InspectionStandardVersion version) => new(
            i.QcInspectionId,
            i.InspectionCode,
            i.ProductBatchId,
            batch.BatchCode,
            batch.ProductName,
            batch.CropType?.CropName ?? string.Empty,
            i.InspectionStandardVersionId,
            version.InspectionStandardSet?.StandardCode ?? string.Empty,
            version.InspectionStandardSet?.StandardName ?? string.Empty,
            version.VersionNo,
            i.QcAccountId,
            i.QcAccount?.FullName ?? string.Empty,
            i.SamplingRatio,
            i.SampleSize,
            i.InspectionStatus,
            i.QcResult,
            i.QualityGrade,
            i.StartedAt,
            i.CompletedAt,
            i.Note);

    private static QcInspectionDetailDto MapToDetailDto(QcInspection i)
    {
        var batch   = i.ProductBatch;
        var version = i.InspectionStandardVersion;

        var criteriaResults = i.ResultDetails.Select(d =>
        {
            var c = d.InspectionCriterion;
            var rules = c?.GradeRules
                .OrderBy(r => Array.IndexOf(GradeOrder, r.Grade))
                .Select(r => new GradeRuleSnapshot(
                    r.Grade, r.MinValue, r.MaxValue, r.RequiredTextValue, r.IsFailRule))
                .ToList() ?? [];
            return new CriterionResultDto(
                d.InspectionResultDetailId,
                d.InspectionCriterionId,
                c?.CriterionCode ?? string.Empty,
                c?.CriterionName ?? string.Empty,
                c?.CriterionGroup ?? string.Empty,
                c?.DataType ?? string.Empty,
                c?.Unit,
                c?.IsRequired ?? false,
                c?.IsCritical ?? false,
                d.NumericValue,
                d.TextValue,
                d.BooleanValue,
                d.EvaluatedGrade,
                d.IsPassed,
                d.Remarks,
                rules);
        }).ToList();

        var images = i.QualityImages.OrderBy(img => img.UploadedAt).Select(img =>
            new QualityImageDto(
                img.QualityImageId,
                img.FileName,
                img.FileUrl,
                img.MimeType,
                img.UploadedByAccountId,
                img.UploadedByAccount?.FullName ?? string.Empty,
                img.UploadedAt)).ToList();

        var envLogs = i.EnvironmentLogs.OrderBy(e => e.RecordedAt).Select(e =>
        {
            bool? outOfRange = null;
            if (batch?.ExpectedMinTempC.HasValue == true || batch?.ExpectedMaxTempC.HasValue == true)
            {
                outOfRange =
                    (batch.ExpectedMinTempC.HasValue && e.TemperatureC < batch.ExpectedMinTempC.Value) ||
                    (batch.ExpectedMaxTempC.HasValue && e.TemperatureC > batch.ExpectedMaxTempC.Value);
            }
            return new EnvironmentLogDto(
                e.EnvironmentLogId,
                e.ProductBatchId,
                e.WarehouseLocationId,
                e.WarehouseLocation?.LocationCode ?? string.Empty,
                e.TemperatureC,
                e.HumidityPct,
                e.SourceType,
                e.SensorIdentifier,
                e.RecordedAt,
                e.Note,
                outOfRange);
        }).ToList();

        var sensory = i.SensoryResult is { } s
            ? new SensoryResultDto(s.SensoryResultId, s.FreshnessScore, s.SizeScore,
                s.ColorScore, s.RipenessScore, s.DamagePercentage, s.Note)
            : null;

        var lab = i.LabResult is { } l
            ? new LabResultDto(l.LabResultId, l.ChemicalResidueStatus, l.ResidueValue,
                l.ResidueUnit, l.PathogenStatus, l.PathogenName, l.LabName, l.TestedAt, l.Note)
            : null;

        return new QcInspectionDetailDto(
            i.QcInspectionId,
            i.InspectionCode,
            i.ProductBatchId,
            batch?.BatchCode ?? string.Empty,
            batch?.ProductName ?? string.Empty,
            batch?.CropType?.CropName ?? string.Empty,
            batch?.ExpectedMinTempC,
            batch?.ExpectedMaxTempC,
            batch?.ExpectedMinHumidityPct,
            batch?.ExpectedMaxHumidityPct,
            i.InspectionStandardVersionId,
            version?.InspectionStandardSet?.StandardCode ?? string.Empty,
            version?.InspectionStandardSet?.StandardName ?? string.Empty,
            version?.VersionNo ?? 0,
            i.QcAccountId,
            i.QcAccount?.FullName ?? string.Empty,
            i.SamplingRatio,
            i.SampleSize,
            i.InspectionStatus,
            i.QcResult,
            i.QualityGrade,
            i.StartedAt,
            i.CompletedAt,
            i.Note,
            sensory,
            lab,
            images,
            criteriaResults,
            envLogs);
    }
}
