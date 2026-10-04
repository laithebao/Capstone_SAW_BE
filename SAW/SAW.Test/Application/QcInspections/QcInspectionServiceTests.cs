using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.QcInspections;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.QcInspections;

/// <summary>
/// Unit tests for:
///   UC20 – Create Inspection Form
///   UC21 – Declare Batch Sampling Ratio
///   UC22 – Input Sensory Inspection Result
///   UC23 – Upload Quality Evidence Image
///   UC24 – Input Laboratory Test Result
///   UC25 – Input Actual Storage Temperature
///   UC52 – Compare Inspection Data with Rule Set  (via FinalizeAsync)
///   UC53 – Classify Product Quality Grade          (via FinalizeAsync)
///   UC54 – Reject Batch with Serious Defect
///   UC62 – View Inspection List
/// </summary>
public sealed class QcInspectionServiceTests
{
    private readonly Mock<IQcInspectionRepository> _repo = new();

    private QcInspectionService CreateService() => new(_repo.Object);

    // ─── Factory helpers ──────────────────────────────────────────────────────

    private static ProductBatch MakeBatch(
        long id = 1,
        string status = "PENDING_QC",
        int cropTypeId = 1,
        decimal weight = 500m) => new()
    {
        ProductBatchId = id,
        BatchCode = $"BATCH-{id:000}",
        ProductName = "Rice",
        CropTypeId = cropTypeId,
        WeightInKg = weight,
        BatchStatus = status,
        CropType = new CropType { CropTypeId = cropTypeId, CropName = "Rice" }
    };

    private static InspectionStandardVersion MakeVersion(
        long versionId = 10,
        string status = "PUBLISHED",
        int cropTypeId = 1,
        IEnumerable<InspectionCriterion>? criteria = null) => new()
    {
        InspectionStandardVersionId = versionId,
        VersionNo = 1,
        VersionStatus = status,
        InspectionStandardSet = new InspectionStandardSet
        {
            InspectionStandardSetId = 1,
            StandardCode = "STD-001",
            StandardName = "Test Standard",
            CropTypeId = cropTypeId
        },
        Criteria = (criteria ?? []).ToList()
    };

    private static InspectionCriterion MakeNumberCriterion(
        long id = 100,
        bool isRequired = true,
        bool isCritical = false,
        decimal minA = 0m,
        decimal maxA = 10m) => new()
    {
        InspectionCriterionId = id,
        CriterionCode = $"CR{id:000}",
        CriterionName = $"Criterion {id}",
        CriterionGroup = "SENSORY",
        DataType = "NUMBER",
        IsRequired = isRequired,
        IsCritical = isCritical,
        GradeRules =
        [
            new CriterionGradeRule { Grade = "A", MinValue = minA, MaxValue = maxA, IsFailRule = false },
            new CriterionGradeRule { Grade = "B", MinValue = maxA + 1, MaxValue = maxA + 10, IsFailRule = isCritical }
        ]
    };

    private static InspectionCriterion MakeBooleanCriterion(long id = 200) => new()
    {
        InspectionCriterionId = id,
        CriterionCode = $"BC{id:000}",
        CriterionName = $"Boolean {id}",
        CriterionGroup = "LAB",
        DataType = "BOOLEAN",
        IsRequired = true,
        IsCritical = true,
        GradeRules = []
    };

    private static InspectionCriterion MakeTextCriterion(long id = 300) => new()
    {
        InspectionCriterionId = id,
        CriterionCode = $"TC{id:000}",
        CriterionName = $"Text {id}",
        CriterionGroup = "SENSORY",
        DataType = "TEXT",
        IsRequired = true,
        IsCritical = false,
        GradeRules =
        [
            new CriterionGradeRule { Grade = "A", RequiredTextValue = "Good", IsFailRule = false },
            new CriterionGradeRule { Grade = "B", RequiredTextValue = "Fair", IsFailRule = false }
        ]
    };

    private static QcInspection MakeInspection(
        long id = 1,
        string status = "IN_PROGRESS",
        ProductBatch? batch = null,
        InspectionStandardVersion? version = null,
        IEnumerable<InspectionResultDetail>? details = null) => new()
    {
        QcInspectionId = id,
        InspectionCode = $"QC-BATCH-001-20260101-{id:0000}",
        ProductBatchId = 1,
        InspectionStandardVersionId = 10,
        QcAccountId = 5,
        InspectionStatus = status,
        StartedAt = DateTime.UtcNow.AddHours(-1),
        ProductBatch = batch ?? MakeBatch(),
        InspectionStandardVersion = version ?? MakeVersion(),
        ResultDetails = (details ?? []).ToList()
    };

    // ═════════════════════════════════════════════════════════════════════════
    // UC20 – Create Inspection Form
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC20_CreateAsync_WithValidRequest_ReturnsDto()
    {
        var batch = MakeBatch();
        var version = MakeVersion(criteria: [MakeNumberCriterion()]);
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _repo.Setup(r => r.GetVersionWithCriteriaAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _repo.Setup(r => r.HasActiveInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _repo.Setup(r => r.InspectionCodeExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _repo.Setup(r => r.AddInspection(It.IsAny<QcInspection>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.CreateAsync(
            new CreateQcInspectionRequest(1, 10, "Note"),
            actorAccountId: 5,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.ProductBatchId);
        Assert.Equal(10, result.InspectionStandardVersionId);
        Assert.Equal("DRAFT", result.InspectionStatus);
        _repo.Verify(r => r.AddInspection(It.IsAny<QcInspection>()), Times.Once);
    }

    [Fact]
    public async Task UC20_CreateAsync_WhenBatchNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetBatchAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((ProductBatch?)null);
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.CreateAsync(new CreateQcInspectionRequest(99, 10, null), 5, CancellationToken.None));
    }

    [Fact]
    public async Task UC20_CreateAsync_WhenBatchNotPendingQC_ThrowsBadRequest()
    {
        var batch = MakeBatch(status: "APPROVED_FOR_STORAGE");
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(new CreateQcInspectionRequest(1, 10, null), 5, CancellationToken.None));
    }

    [Fact]
    public async Task UC20_CreateAsync_WhenVersionNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(MakeBatch());
        _repo.Setup(r => r.GetVersionWithCriteriaAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((InspectionStandardVersion?)null);
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.CreateAsync(new CreateQcInspectionRequest(1, 99, null), 5, CancellationToken.None));
    }

    [Fact]
    public async Task UC20_CreateAsync_WhenVersionNotPublished_ThrowsBadRequest()
    {
        var batch = MakeBatch();
        var version = MakeVersion(status: "DRAFT");
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _repo.Setup(r => r.GetVersionWithCriteriaAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(new CreateQcInspectionRequest(1, 10, null), 5, CancellationToken.None));
    }

    [Fact]
    public async Task UC20_CreateAsync_WhenVersionCropTypeMismatch_ThrowsBadRequest()
    {
        var batch = MakeBatch(cropTypeId: 1);
        var version = MakeVersion(cropTypeId: 2);  // different crop type
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _repo.Setup(r => r.GetVersionWithCriteriaAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(new CreateQcInspectionRequest(1, 10, null), 5, CancellationToken.None));
    }

    [Fact]
    public async Task UC20_CreateAsync_WhenActiveInspectionExists_ThrowsConflict()
    {
        var batch = MakeBatch();
        var version = MakeVersion();
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _repo.Setup(r => r.GetVersionWithCriteriaAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _repo.Setup(r => r.HasActiveInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var svc = CreateService();

        await Assert.ThrowsAsync<ConflictException>(
            () => svc.CreateAsync(new CreateQcInspectionRequest(1, 10, null), 5, CancellationToken.None));
    }

    [Fact]
    public async Task UC20_CreateAsync_PrePopulatesResultDetailsForEachCriterion()
    {
        var criterion1 = MakeNumberCriterion(100);
        var criterion2 = MakeNumberCriterion(101);
        var version = MakeVersion(criteria: [criterion1, criterion2]);
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(MakeBatch());
        _repo.Setup(r => r.GetVersionWithCriteriaAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _repo.Setup(r => r.HasActiveInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _repo.Setup(r => r.InspectionCodeExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        QcInspection? savedInspection = null;
        _repo.Setup(r => r.AddInspection(It.IsAny<QcInspection>()))
             .Callback<QcInspection>(i => savedInspection = i);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.CreateAsync(new CreateQcInspectionRequest(1, 10, null), 5, CancellationToken.None);

        Assert.NotNull(savedInspection);
        Assert.Equal(2, savedInspection.ResultDetails.Count);
        Assert.Contains(savedInspection.ResultDetails, d => d.InspectionCriterionId == 100);
        Assert.Contains(savedInspection.ResultDetails, d => d.InspectionCriterionId == 101);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC21 – Declare Batch Sampling Ratio
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_WithValidRatio_UpdatesInspection()
    {
        // batch weight 500 kg → minRatio = 0.05 (5%)
        var inspection = MakeInspection(status: "DRAFT", batch: MakeBatch(weight: 500m));
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.UpdateSamplingRatioAsync(1, new UpdateSamplingRatioRequest(0.10m), 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal(0.10m, inspection.SamplingRatio);
        Assert.Equal(50m, inspection.SampleSize);  // 500 * 0.10
        Assert.Equal("IN_PROGRESS", inspection.InspectionStatus);  // DRAFT → IN_PROGRESS
    }

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_WhenInspectionNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetInspectionAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((QcInspection?)null);
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.UpdateSamplingRatioAsync(99, new UpdateSamplingRatioRequest(0.10m), 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_WhenInspectionCompleted_ThrowsBadRequest()
    {
        var inspection = MakeInspection(status: "COMPLETED");
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.UpdateSamplingRatioAsync(1, new UpdateSamplingRatioRequest(0.10m), 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_WhenRatioIsZero_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.UpdateSamplingRatioAsync(1, new UpdateSamplingRatioRequest(0m), 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_WhenRatioExceedsOne_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.UpdateSamplingRatioAsync(1, new UpdateSamplingRatioRequest(1.01m), 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_SmallBatch_RequiresMinimum5Percent()
    {
        // batch < 1000 kg → min ratio = 0.05
        var inspection = MakeInspection(batch: MakeBatch(weight: 800m), status: "DRAFT");
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.UpdateSamplingRatioAsync(1, new UpdateSamplingRatioRequest(0.04m), 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_LargeBatch_RequiresMinimum3Percent()
    {
        // batch >= 1000 kg → min ratio = 0.03
        var inspection = MakeInspection(batch: MakeBatch(weight: 1000m), status: "DRAFT");
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.UpdateSamplingRatioAsync(1, new UpdateSamplingRatioRequest(0.02m), 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC21_UpdateSamplingRatioAsync_WhenAlreadyInProgress_StatusUnchanged()
    {
        var inspection = MakeInspection(status: "IN_PROGRESS", batch: MakeBatch(weight: 500m));
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.UpdateSamplingRatioAsync(1, new UpdateSamplingRatioRequest(0.10m), 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("IN_PROGRESS", inspection.InspectionStatus);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC22 – Input Sensory Inspection Result
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC22_SaveSensoryResultAsync_WithValidData_CreatesSensoryResult()
    {
        var criterion = MakeNumberCriterion(100);
        criterion.CriterionGroup = "SENSORY";
        var detail = new InspectionResultDetail { InspectionCriterionId = 100, InspectionCriterion = criterion };
        var inspection = MakeInspection(details: [detail]);
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.AddSensoryResult(It.IsAny<SensoryResult>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var request = new SaveSensoryResultRequest(
            FreshnessScore: 85m,
            SizeScore: 90m,
            ColorScore: 75m,
            RipenessScore: 80m,
            DamagePercentage: 5m,
            Note: "Sensory OK",
            CriteriaResults: [new(100, 7m, null, null, null)]);

        await svc.SaveSensoryResultAsync(1, request, 5, "ADMINISTRATOR", CancellationToken.None);

        _repo.Verify(r => r.AddSensoryResult(It.IsAny<SensoryResult>()), Times.Once);
    }

    [Fact]
    public async Task UC22_SaveSensoryResultAsync_WhenInspectionDraft_ThrowsBadRequest()
    {
        var inspection = MakeInspection(status: "DRAFT");
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveSensoryResultAsync(1, new SaveSensoryResultRequest(null, null, null, null, null, null, []), 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC22_SaveSensoryResultAsync_WhenScoreOutOfRange_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        // FreshnessScore = 101 (out of [0,100])
        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveSensoryResultAsync(1,
                new SaveSensoryResultRequest(101m, null, null, null, null, null, []),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC22_SaveSensoryResultAsync_WhenDamagePercentageNegative_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveSensoryResultAsync(1,
                new SaveSensoryResultRequest(null, null, null, null, -1m, null, []),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC22_SaveSensoryResultAsync_WhenSensoryResultExists_UpdatesExisting()
    {
        var inspection = MakeInspection();
        inspection.SensoryResult = new SensoryResult
        {
            SensoryResultId = 1,
            QcInspectionId = 1,
            FreshnessScore = 50m
        };
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.SaveSensoryResultAsync(1,
            new SaveSensoryResultRequest(80m, null, null, null, null, null, []),
            5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal(80m, inspection.SensoryResult.FreshnessScore);
        // AddSensoryResult should NOT be called since it already exists
        _repo.Verify(r => r.AddSensoryResult(It.IsAny<SensoryResult>()), Times.Never);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC23 – Upload Quality Evidence Image
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC23_AddImageAsync_WithValidRequest_ReturnsDto()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.CountImagesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _repo.Setup(r => r.AddImage(It.IsAny<QualityImage>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.AddImageAsync(1,
            new UploadQualityImageRequest("photo.jpg", "https://res.cloudinary.com/sample/image/upload/photo.jpg", "image/jpeg"),
            5, "ADMINISTRATOR", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("photo.jpg", result.FileName);
        _repo.Verify(r => r.AddImage(It.IsAny<QualityImage>()), Times.Once);
    }

    [Fact]
    public async Task UC23_AddImageAsync_WithInvalidMimeType_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.AddImageAsync(1,
                new UploadQualityImageRequest("photo.gif", "https://res.cloudinary.com/sample/photo.gif", "image/gif"),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC23_AddImageAsync_WithNonCloudinaryUrl_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.AddImageAsync(1,
                new UploadQualityImageRequest("photo.jpg", "https://example.com/photo.jpg", "image/jpeg"),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC23_AddImageAsync_WhenMaxImagesReached_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.CountImagesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(20); // max
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.AddImageAsync(1,
                new UploadQualityImageRequest("photo.jpg", "https://res.cloudinary.com/sample/photo.jpg", "image/jpeg"),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC23_DeleteImageAsync_WithValidRequest_RemovesImage()
    {
        var inspection = MakeInspection();
        var image = new QualityImage { QualityImageId = 50, QcInspectionId = 1 };
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.GetImageAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(image);
        _repo.Setup(r => r.RemoveImage(image));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.DeleteImageAsync(1, 50, 5, "ADMINISTRATOR", CancellationToken.None);

        _repo.Verify(r => r.RemoveImage(image), Times.Once);
    }

    [Fact]
    public async Task UC23_DeleteImageAsync_WhenImageBelongsToDifferentInspection_ThrowsBadRequest()
    {
        var inspection = MakeInspection(id: 1);
        var image = new QualityImage { QualityImageId = 50, QcInspectionId = 999 }; // different inspection
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.GetImageAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(image);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.DeleteImageAsync(1, 50, 5, "ADMINISTRATOR", CancellationToken.None));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC24 – Input Laboratory Test Result
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC24_SaveLabResultAsync_WithValidData_CreatesLabResult()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.AddLabResult(It.IsAny<LabResult>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.SaveLabResultAsync(1,
            new SaveLabResultRequest("PASS", null, null, "PASS", null, "LabA", null, "All clear", []),
            5, "ADMINISTRATOR", CancellationToken.None);

        _repo.Verify(r => r.AddLabResult(It.IsAny<LabResult>()), Times.Once);
    }

    [Fact]
    public async Task UC24_SaveLabResultAsync_WhenInvalidChemicalResidueStatus_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveLabResultAsync(1,
                new SaveLabResultRequest("INVALID", null, null, null, null, null, null, null, []),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC24_SaveLabResultAsync_WhenChemicalFailWithoutResidueValue_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveLabResultAsync(1,
                new SaveLabResultRequest("FAIL", null, null, null, null, null, null, null, []),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC24_SaveLabResultAsync_WhenPathogenFailWithoutPathogenName_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveLabResultAsync(1,
                new SaveLabResultRequest(null, null, null, "FAIL", null, null, null, null, []),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC24_SaveLabResultAsync_WhenResidueValueIsNegative_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveLabResultAsync(1,
                new SaveLabResultRequest(null, -1m, null, null, null, null, null, null, []),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC24_SaveLabResultAsync_WhenTestedAtIsFuture_ThrowsBadRequest()
    {
        var inspection = MakeInspection();
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.SaveLabResultAsync(1,
                new SaveLabResultRequest(null, null, null, null, null, null, DateTime.UtcNow.AddDays(1), null, []),
                5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC24_SaveLabResultAsync_WhenLabResultExists_UpdatesExisting()
    {
        var inspection = MakeInspection();
        inspection.LabResult = new LabResult
        {
            LabResultId = 1,
            QcInspectionId = 1,
            ChemicalResidueStatus = "NOT_TESTED"
        };
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.SaveLabResultAsync(1,
            new SaveLabResultRequest("PASS", null, null, null, null, null, null, null, []),
            5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("PASS", inspection.LabResult.ChemicalResidueStatus);
        _repo.Verify(r => r.AddLabResult(It.IsAny<LabResult>()), Times.Never);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC25 – Input Actual Storage Temperature
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC25_CreateEnvironmentLogAsync_WithValidData_ReturnsDto()
    {
        var batch = MakeBatch();
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _repo.Setup(r => r.AddEnvironmentLog(It.IsAny<EnvironmentLog>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.CreateEnvironmentLogAsync(
            new CreateEnvironmentLogRequest(
                ProductBatchId: 1,
                WarehouseLocationId: null,
                QcInspectionId: null,
                TemperatureC: 20m,
                HumidityPct: 60m,
                SourceType: "MANUAL",
                SensorIdentifier: null,
                RecordedAt: DateTime.UtcNow.AddMinutes(-5),
                Note: null),
            actorAccountId: 5,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(20m, result.TemperatureC);
        Assert.Equal(60m, result.HumidityPct);
        Assert.Equal("MANUAL", result.SourceType);
        _repo.Verify(r => r.AddEnvironmentLog(It.IsAny<EnvironmentLog>()), Times.Once);
    }

    [Fact]
    public async Task UC25_CreateEnvironmentLogAsync_WithInvalidSourceType_ThrowsBadRequest()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateEnvironmentLogAsync(
                new CreateEnvironmentLogRequest(1, null, null, 20m, null, "INVALID_SOURCE", null, DateTime.UtcNow, null),
                5, CancellationToken.None));
    }

    [Fact]
    public async Task UC25_CreateEnvironmentLogAsync_WhenHumidityOutOfRange_ThrowsBadRequest()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateEnvironmentLogAsync(
                new CreateEnvironmentLogRequest(1, null, null, 20m, 101m, "MANUAL", null, DateTime.UtcNow, null),
                5, CancellationToken.None));
    }

    [Fact]
    public async Task UC25_CreateEnvironmentLogAsync_WhenRecordedAtIsFuture_ThrowsBadRequest()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateEnvironmentLogAsync(
                new CreateEnvironmentLogRequest(1, null, null, 20m, null, "MANUAL", null,
                    DateTime.UtcNow.AddHours(1), null),
                5, CancellationToken.None));
    }

    [Fact]
    public async Task UC25_CreateEnvironmentLogAsync_WhenBatchNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetBatchAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((ProductBatch?)null);
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.CreateEnvironmentLogAsync(
                new CreateEnvironmentLogRequest(99, null, null, 20m, null, "MANUAL", null, DateTime.UtcNow.AddMinutes(-1), null),
                5, CancellationToken.None));
    }

    [Fact]
    public async Task UC25_CreateEnvironmentLogAsync_WithInvalidWarehouseLocation_ThrowsNotFound()
    {
        _repo.Setup(r => r.WarehouseLocationExistsAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.CreateEnvironmentLogAsync(
                new CreateEnvironmentLogRequest(1, 99, null, 20m, null, "MANUAL", null, DateTime.UtcNow.AddMinutes(-1), null),
                5, CancellationToken.None));
    }

    [Fact]
    public async Task UC25_CreateEnvironmentLogAsync_WhenTempOutOfRange_FlagIsTrue()
    {
        var batch = MakeBatch();
        batch.ExpectedMinTempC = 15m;
        batch.ExpectedMaxTempC = 25m;
        _repo.Setup(r => r.GetBatchAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _repo.Setup(r => r.AddEnvironmentLog(It.IsAny<EnvironmentLog>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.CreateEnvironmentLogAsync(
            new CreateEnvironmentLogRequest(1, null, null, 30m, null, "IOT_SENSOR", null, DateTime.UtcNow.AddMinutes(-1), null),
            5, CancellationToken.None);

        Assert.True(result.IsTempOutOfRange);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC52 + UC53 – Compare with Rule Set & Classify Grade (FinalizeAsync)
    // ═════════════════════════════════════════════════════════════════════════

    private static QcInspection BuildFinalizeInspection(
        IEnumerable<(InspectionCriterion criterion, InspectionResultDetail detail)> entries,
        string batchStatus = "PENDING_QC")
    {
        var version = new InspectionStandardVersion
        {
            InspectionStandardVersionId = 10,
            VersionNo = 1,
            VersionStatus = "PUBLISHED",
            InspectionStandardSet = new InspectionStandardSet
            {
                InspectionStandardSetId = 1,
                StandardCode = "STD-001",
                StandardName = "Test Standard"
            }
        };

        foreach (var (criterion, _) in entries)
            version.Criteria.Add(criterion);

        var batch = MakeBatch(status: batchStatus);
        var inspection = new QcInspection
        {
            QcInspectionId = 1,
            InspectionCode = "QC-BATCH-001-20260101-1234",
            ProductBatchId = 1,
            InspectionStandardVersionId = 10,
            QcAccountId = 5,
            InspectionStatus = "IN_PROGRESS",
            StartedAt = DateTime.UtcNow.AddHours(-1),
            ProductBatch = batch,
            InspectionStandardVersion = version
        };

        foreach (var (criterion, detail) in entries)
        {
            detail.InspectionCriterion = criterion;
            detail.InspectionCriterionId = criterion.InspectionCriterionId;
            inspection.ResultDetails.Add(detail);
        }

        return inspection;
    }

    [Fact]
    public async Task UC52_UC53_FinalizeAsync_AllGradeA_ReturnsPASSGradeA()
    {
        var criterion = MakeNumberCriterion(100, isRequired: true, isCritical: false);
        var detail = new InspectionResultDetail
        {
            InspectionResultDetailId = 1,
            InspectionCriterionId = 100,
            NumericValue = 5m  // within A range [0,10]
        };
        var inspection = BuildFinalizeInspection([(criterion, detail)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("PASS", result.QcResult);
        Assert.Equal("A", result.QualityGrade);
        Assert.Equal("APPROVED_FOR_STORAGE", result.NewBatchStatus);
    }

    [Fact]
    public async Task UC53_FinalizeAsync_WorstGradeDeterminesOverallGrade()
    {
        var criterionA = MakeNumberCriterion(100, isCritical: false);
        var criterionB = new InspectionCriterion
        {
            InspectionCriterionId = 101,
            CriterionCode = "CR101",
            CriterionName = "Criterion 101",
            CriterionGroup = "SENSORY",
            DataType = "NUMBER",
            IsRequired = true,
            IsCritical = false,
            GradeRules =
            [
                new CriterionGradeRule { Grade = "A", MinValue = 0m, MaxValue = 5m, IsFailRule = false },
                new CriterionGradeRule { Grade = "B", MinValue = 6m, MaxValue = 10m, IsFailRule = false },
                new CriterionGradeRule { Grade = "C", MinValue = 11m, MaxValue = 20m, IsFailRule = false }
            ]
        };
        var detailA = new InspectionResultDetail { InspectionCriterionId = 100, NumericValue = 5m }; // grade A
        var detailB = new InspectionResultDetail { InspectionCriterionId = 101, NumericValue = 15m }; // grade C (worst)
        var inspection = BuildFinalizeInspection([(criterionA, detailA), (criterionB, detailB)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("PASS", result.QcResult);
        Assert.Equal("C", result.QualityGrade);
    }

    [Fact]
    public async Task UC52_FinalizeAsync_CriticalCriterionFails_ReturnsFAIL()
    {
        var criterion = MakeNumberCriterion(100, isRequired: true, isCritical: true);
        var detail = new InspectionResultDetail
        {
            InspectionCriterionId = 100,
            NumericValue = 15m  // falls in grade B [11-20] which is fail rule for critical
        };
        var inspection = BuildFinalizeInspection([(criterion, detail)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("FAIL", result.QcResult);
        Assert.Equal("REJECTED", result.NewBatchStatus);
    }

    [Fact]
    public async Task UC52_FinalizeAsync_BooleanCriticalFails_ReturnsFAIL()
    {
        var criterion = MakeBooleanCriterion(200);
        var detail = new InspectionResultDetail
        {
            InspectionCriterionId = 200,
            BooleanValue = false  // FAIL
        };
        var inspection = BuildFinalizeInspection([(criterion, detail)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("FAIL", result.QcResult);
        Assert.Null(result.QualityGrade);
    }

    [Fact]
    public async Task UC53_FinalizeAsync_GradeDOrE_BatchGoesToQuarantine()
    {
        var criterion = new InspectionCriterion
        {
            InspectionCriterionId = 100,
            CriterionCode = "CR100",
            CriterionName = "Criterion",
            CriterionGroup = "SENSORY",
            DataType = "NUMBER",
            IsRequired = true,
            IsCritical = false,
            GradeRules =
            [
                new CriterionGradeRule { Grade = "A", MinValue = 0m, MaxValue = 5m, IsFailRule = false },
                new CriterionGradeRule { Grade = "B", MinValue = 6m, MaxValue = 10m, IsFailRule = false },
                new CriterionGradeRule { Grade = "C", MinValue = 11m, MaxValue = 20m, IsFailRule = false },
                new CriterionGradeRule { Grade = "D", MinValue = 21m, MaxValue = 30m, IsFailRule = false }
            ]
        };
        var detail = new InspectionResultDetail { InspectionCriterionId = 100, NumericValue = 25m }; // grade D
        var inspection = BuildFinalizeInspection([(criterion, detail)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("PASS", result.QcResult);
        Assert.Equal("D", result.QualityGrade);
        Assert.Equal("QUARANTINE", result.NewBatchStatus);
    }

    [Fact]
    public async Task UC52_FinalizeAsync_WhenAlreadyCompleted_ThrowsBadRequest()
    {
        var inspection = MakeInspection(status: "COMPLETED");
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC52_FinalizeAsync_WhenStillDraft_ThrowsBadRequest()
    {
        var inspection = MakeInspection(status: "DRAFT");
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC52_FinalizeAsync_WhenRequiredCriterionHasNoValue_ThrowsBadRequest()
    {
        var criterion = MakeNumberCriterion(100, isRequired: true);
        // No NumericValue set → required but missing
        var detail = new InspectionResultDetail { InspectionCriterionId = 100 };
        var inspection = BuildFinalizeInspection([(criterion, detail)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None));
    }

    [Fact]
    public async Task UC52_FinalizeAsync_BooleanTruePass_DoesNotAffectOverallGrade()
    {
        var boolCriterion = MakeBooleanCriterion(200);
        var numCriterion = MakeNumberCriterion(100, isCritical: false);
        var boolDetail = new InspectionResultDetail { InspectionCriterionId = 200, BooleanValue = true };
        var numDetail = new InspectionResultDetail { InspectionCriterionId = 100, NumericValue = 5m }; // A
        var inspection = BuildFinalizeInspection([(boolCriterion, boolDetail), (numCriterion, numDetail)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("PASS", result.QcResult);
        Assert.Equal("A", result.QualityGrade);  // BOOLEAN not graded, A from NUMBER
    }

    [Fact]
    public async Task UC52_FinalizeAsync_TextCriterionMatchesGrade_ReturnsCorrectGrade()
    {
        var criterion = MakeTextCriterion(300);
        var detail = new InspectionResultDetail { InspectionCriterionId = 300, TextValue = "Fair" }; // matches grade B
        var inspection = BuildFinalizeInspection([(criterion, detail)]);
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.FinalizeAsync(1, 5, "ADMINISTRATOR", CancellationToken.None);

        Assert.Equal("PASS", result.QcResult);
        Assert.Equal("B", result.QualityGrade);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC54 – Reject Batch with Serious Defect
    // ═════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("PENDING_QC")]
    [InlineData("QUARANTINE")]
    [InlineData("APPROVED_FOR_STORAGE")]
    public async Task UC54_RejectBatchAsync_WithRejectableStatus_RejectsBatch(string batchStatus)
    {
        var inspection = MakeInspection(batch: MakeBatch(status: batchStatus));
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.RejectBatchAsync(1, new RejectBatchRequest("Serious defect found"), 5, CancellationToken.None);

        Assert.Equal("REJECTED", inspection.ProductBatch.BatchStatus);
        Assert.Equal("Serious defect found", inspection.ProductBatch.RejectionReason);
    }

    [Fact]
    public async Task UC54_RejectBatchAsync_WhenBatchAlreadyRejected_ThrowsBadRequest()
    {
        var inspection = MakeInspection(batch: MakeBatch(status: "REJECTED"));
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.RejectBatchAsync(1, new RejectBatchRequest("Reason"), 5, CancellationToken.None));
    }

    [Fact]
    public async Task UC54_RejectBatchAsync_WhenInspectionNotCompleted_CompletesItAsFail()
    {
        var inspection = MakeInspection(status: "IN_PROGRESS", batch: MakeBatch(status: "PENDING_QC"));
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.RejectBatchAsync(1, new RejectBatchRequest("Serious defect"), 5, CancellationToken.None);

        Assert.Equal("COMPLETED", inspection.InspectionStatus);
        Assert.Equal("FAIL", inspection.QcResult);
    }

    [Fact]
    public async Task UC54_RejectBatchAsync_WhenInspectionNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetInspectionDetailAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((QcInspection?)null);
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.RejectBatchAsync(99, new RejectBatchRequest("Reason"), 5, CancellationToken.None));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC62 – View Inspection List
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC62_ListAsync_AsQcStaff_FiltersToOwnInspections()
    {
        var items = new List<QcInspection>
        {
            MakeInspection(id: 1),
            MakeInspection(id: 2)
        };
        _repo.Setup(r => r.ListAsync(
            null, null, null, null,
            5, // filtered to actorAccountId = 5
            null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<QcInspection>)items, 2));

        var svc = CreateService();
        var result = await svc.ListAsync(
            new QcInspectionListQuery(null, null, null, null, null, null, null),
            actorAccountId: 5,
            actorRole: "QC_STAFF",
            CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);

        // Verify the repo was called with the QC_STAFF's own account ID
        _repo.Verify(r => r.ListAsync(null, null, null, null, 5, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UC62_ListAsync_AsAdmin_CanSeeAllInspections()
    {
        var items = new List<QcInspection> { MakeInspection(id: 1) };
        _repo.Setup(r => r.ListAsync(null, null, null, null, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<QcInspection>)items, 1));

        var svc = CreateService();
        var result = await svc.ListAsync(
            new QcInspectionListQuery(null, null, null, null, null, null, null),
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        _repo.Verify(r => r.ListAsync(null, null, null, null, null, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UC62_ListAsync_PageSizeIsCappedAt50()
    {
        _repo.Setup(r => r.ListAsync(null, null, null, null, null, null, null, 1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<QcInspection>)[MakeInspection()], 1));

        var svc = CreateService();
        await svc.ListAsync(
            new QcInspectionListQuery(null, null, null, null, null, null, null, Page: 1, PageSize: 200),
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);

        // Should cap at 50
        _repo.Verify(r => r.ListAsync(null, null, null, null, null, null, null, 1, 50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UC62_ListAsync_ReturnsPagedResult()
    {
        var items = new List<QcInspection> { MakeInspection(id: 1) };
        _repo.Setup(r => r.ListAsync(null, null, null, null, null, null, null, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<QcInspection>)items, 100));

        var svc = CreateService();
        var result = await svc.ListAsync(
            new QcInspectionListQuery(null, null, null, null, null, null, null, Page: 2, PageSize: 10),
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);

        Assert.Equal(100, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Single(result.Items);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Authorization / Ownership tests
// ─────────────────────────────────────────────────────────────────────────────
// Kiểm tra logic EnsureOwnership trong service:
//   • QC_STAFF chỉ được thao tác trên phiếu do chính mình tạo (QcAccountId == actorId)
//   • ADMINISTRATOR không bị giới hạn
//   • WAREHOUSE_MANAGER được đọc (GET) nhưng bị chặn ở controller trước khi
//     vào service (không test ở đây vì đó là controller-level Authorize attribute)
// ═════════════════════════════════════════════════════════════════════════════
public sealed class QcInspectionAuthorizationTests
{
    private readonly Mock<IQcInspectionRepository> _repo = new();
    private QcInspectionService CreateService() => new(_repo.Object);

    // Phiếu tạo bởi accountId = 5
    private static QcInspection OwnerInspection() => new()
    {
        QcInspectionId = 1,
        InspectionCode = "QC-BATCH-001-20260101-0001",
        ProductBatchId = 1,
        InspectionStandardVersionId = 10,
        QcAccountId = 5,             // owner
        InspectionStatus = "IN_PROGRESS",
        StartedAt = DateTime.UtcNow.AddHours(-1),
        ProductBatch = new ProductBatch { ProductBatchId = 1, WeightInKg = 500m, CropTypeId = 1 },
        InspectionStandardVersion = new InspectionStandardVersion
        {
            InspectionStandardVersionId = 10,
            VersionStatus = "PUBLISHED",
            InspectionStandardSet = new InspectionStandardSet { InspectionStandardSetId = 1 }
        }
    };

    private void SetupGetInspection(QcInspection inspection) =>
        _repo.Setup(r => r.GetInspectionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);

    private void SetupGetInspectionDetail(QcInspection inspection) =>
        _repo.Setup(r => r.GetInspectionDetailAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);

    private void SetupSave() =>
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

    // ── UC21 UpdateSamplingRatio ──────────────────────────────────────────────

    [Fact]
    public async Task UC21_QcStaff_AccessingOtherOwnersInspection_ThrowsForbidden()
    {
        SetupGetInspection(OwnerInspection());  // owner = 5
        var svc = CreateService();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.UpdateSamplingRatioAsync(1,
                new UpdateSamplingRatioRequest(0.10m),
                actorAccountId: 99,  // khác owner
                actorRole: "QC_STAFF",
                CancellationToken.None));
    }

    [Fact]
    public async Task UC21_QcStaff_AccessingOwnInspection_Succeeds()
    {
        var inspection = OwnerInspection();
        inspection.InspectionStatus = "DRAFT";
        SetupGetInspection(inspection);
        SetupSave();

        var svc = CreateService();
        // actorAccountId = 5 = QcAccountId → owner → không throw
        await svc.UpdateSamplingRatioAsync(1,
            new UpdateSamplingRatioRequest(0.10m),
            actorAccountId: 5,
            actorRole: "QC_STAFF",
            CancellationToken.None);
    }

    [Fact]
    public async Task UC21_Administrator_AccessingAnyInspection_Succeeds()
    {
        var inspection = OwnerInspection();
        inspection.InspectionStatus = "DRAFT";
        SetupGetInspection(inspection);
        SetupSave();

        var svc = CreateService();
        await svc.UpdateSamplingRatioAsync(1,
            new UpdateSamplingRatioRequest(0.10m),
            actorAccountId: 1,   // khác owner, nhưng là ADMINISTRATOR
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);
    }

    // ── UC22 SaveSensoryResult ────────────────────────────────────────────────

    [Fact]
    public async Task UC22_QcStaff_AccessingOtherOwnersInspection_ThrowsForbidden()
    {
        SetupGetInspection(OwnerInspection());
        var svc = CreateService();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.SaveSensoryResultAsync(1,
                new SaveSensoryResultRequest(null, null, null, null, null, null, []),
                actorAccountId: 99,
                actorRole: "QC_STAFF",
                CancellationToken.None));
    }

    [Fact]
    public async Task UC22_Administrator_AccessingAnyInspection_Succeeds()
    {
        SetupGetInspection(OwnerInspection());
        SetupSave();
        var svc = CreateService();

        // ADMINISTRATOR không bị ForbiddenException, chỉ bị BadRequest do status IN_PROGRESS check
        // (không phải DRAFT nên không ném BadRequest từ EnsureInProgress)
        await svc.SaveSensoryResultAsync(1,
            new SaveSensoryResultRequest(null, null, null, null, null, null, []),
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);
    }

    // ── UC23 AddImage / DeleteImage ───────────────────────────────────────────

    [Fact]
    public async Task UC23_AddImage_QcStaff_AccessingOtherOwnersInspection_ThrowsForbidden()
    {
        SetupGetInspection(OwnerInspection());
        var svc = CreateService();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.AddImageAsync(1,
                new UploadQualityImageRequest("photo.jpg", "https://res.cloudinary.com/x/photo.jpg", "image/jpeg"),
                actorAccountId: 99,
                actorRole: "QC_STAFF",
                CancellationToken.None));
    }

    [Fact]
    public async Task UC23_DeleteImage_QcStaff_AccessingOtherOwnersInspection_ThrowsForbidden()
    {
        SetupGetInspection(OwnerInspection());
        var svc = CreateService();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.DeleteImageAsync(1, 50,
                actorAccountId: 99,
                actorRole: "QC_STAFF",
                CancellationToken.None));
    }

    // ── UC24 SaveLabResult ────────────────────────────────────────────────────

    [Fact]
    public async Task UC24_QcStaff_AccessingOtherOwnersInspection_ThrowsForbidden()
    {
        SetupGetInspection(OwnerInspection());
        var svc = CreateService();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.SaveLabResultAsync(1,
                new SaveLabResultRequest(null, null, null, null, null, null, null, null, []),
                actorAccountId: 99,
                actorRole: "QC_STAFF",
                CancellationToken.None));
    }

    [Fact]
    public async Task UC24_Administrator_AccessingAnyInspection_Succeeds()
    {
        SetupGetInspection(OwnerInspection());
        SetupSave();
        var svc = CreateService();

        await svc.SaveLabResultAsync(1,
            new SaveLabResultRequest(null, null, null, null, null, null, null, null, []),
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);
    }

    // ── UC52/53 FinalizeAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UC52_QcStaff_AccessingOtherOwnersInspection_ThrowsForbidden()
    {
        SetupGetInspectionDetail(OwnerInspection());
        var svc = CreateService();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.FinalizeAsync(1,
                actorAccountId: 99,
                actorRole: "QC_STAFF",
                CancellationToken.None));
    }

    [Fact]
    public async Task UC52_QcStaff_FinalizingOwnInspection_PassesOwnershipCheck()
    {
        // ownership passes (QcAccountId=5 == actor=5), then service proceeds normally
        var inspection = OwnerInspection();
        SetupGetInspectionDetail(inspection);
        SetupSave();
        var svc = CreateService();

        // No ForbiddenException — ownership check passes. Business logic runs normally.
        var result = await svc.FinalizeAsync(1,
            actorAccountId: 5,
            actorRole: "QC_STAFF",
            CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task UC52_Administrator_AccessingAnyInspection_PassesOwnershipCheck()
    {
        // ADMINISTRATOR: ownership skipped entirely, service proceeds normally
        SetupGetInspectionDetail(OwnerInspection());
        SetupSave();
        var svc = CreateService();

        // No ForbiddenException — ownership check not applied to ADMINISTRATOR
        var result = await svc.FinalizeAsync(1,
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);

        Assert.NotNull(result);
    }

    // ── GetByIdAsync (detail view) ────────────────────────────────────────────

    [Fact]
    public async Task GetById_QcStaff_AccessingOtherOwnersInspection_ThrowsForbidden()
    {
        SetupGetInspectionDetail(OwnerInspection());
        var svc = CreateService();

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.GetByIdAsync(1,
                actorAccountId: 99,
                actorRole: "QC_STAFF",
                CancellationToken.None));
    }

    [Fact]
    public async Task GetById_QcStaff_AccessingOwnInspection_Succeeds()
    {
        SetupGetInspectionDetail(OwnerInspection());
        var svc = CreateService();

        var result = await svc.GetByIdAsync(1,
            actorAccountId: 5,   // owner
            actorRole: "QC_STAFF",
            CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetById_WarehouseManager_AccessingAnyInspection_Succeeds()
    {
        // WAREHOUSE_MANAGER được đọc phiếu của bất kỳ ai
        SetupGetInspectionDetail(OwnerInspection());
        var svc = CreateService();

        var result = await svc.GetByIdAsync(1,
            actorAccountId: 99,  // khác owner
            actorRole: "WAREHOUSE_MANAGER",
            CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetById_Administrator_AccessingAnyInspection_Succeeds()
    {
        SetupGetInspectionDetail(OwnerInspection());
        var svc = CreateService();

        var result = await svc.GetByIdAsync(1,
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);

        Assert.NotNull(result);
    }

    // ── UC62 ListAsync – role-based filter ────────────────────────────────────

    [Fact]
    public async Task UC62_QcStaff_ListIsFilteredToOwnAccountId()
    {
        var items = new List<QcInspection> { OwnerInspection() };
        // Service phải gọi ListAsync với ownerId = 5 (actorAccountId)
        _repo.Setup(r => r.ListAsync(null, null, null, null, 5, null, null, 1, 20, It.IsAny<CancellationToken>()))
             .ReturnsAsync(((IReadOnlyList<QcInspection>)items, 1));

        var svc = CreateService();
        var result = await svc.ListAsync(
            new QcInspectionListQuery(null, null, null, null, null, null, null),
            actorAccountId: 5,
            actorRole: "QC_STAFF",
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        _repo.Verify(r => r.ListAsync(null, null, null, null, 5, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UC62_WarehouseManager_ListIsNotFilteredByAccountId()
    {
        var items = new List<QcInspection> { OwnerInspection() };
        // WAREHOUSE_MANAGER: không filter theo accountId (null)
        _repo.Setup(r => r.ListAsync(null, null, null, null, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
             .ReturnsAsync(((IReadOnlyList<QcInspection>)items, 1));

        var svc = CreateService();
        var result = await svc.ListAsync(
            new QcInspectionListQuery(null, null, null, null, null, null, null),
            actorAccountId: 99,
            actorRole: "WAREHOUSE_MANAGER",
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        _repo.Verify(r => r.ListAsync(null, null, null, null, null, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UC62_Administrator_ListIsNotFilteredByAccountId()
    {
        var items = new List<QcInspection> { OwnerInspection() };
        _repo.Setup(r => r.ListAsync(null, null, null, null, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
             .ReturnsAsync(((IReadOnlyList<QcInspection>)items, 1));

        var svc = CreateService();
        var result = await svc.ListAsync(
            new QcInspectionListQuery(null, null, null, null, null, null, null),
            actorAccountId: 1,
            actorRole: "ADMINISTRATOR",
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
    }
}
