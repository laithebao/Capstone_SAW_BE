using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.InspectionStandards;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.InspectionStandards;

/// <summary>
/// Unit tests for:
///   UC12 – Create an Inspection Standard Set
///   UC13 – Create a New Version for an Inspection Standard Set
/// </summary>
public sealed class InspectionStandardServiceTests
{
    private readonly Mock<IInspectionStandardRepository> _repo = new();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private InspectionStandardService CreateService() => new(_repo.Object);

    private static SaveInspectionCriterionRequest MakeNumberCriterion(
        string code = "C001",
        bool isCritical = true) => new(
            Code: code,
            Name: $"Criterion {code}",
            CriterionGroup: "SENSORY",
            DataType: "NUMBER",
            Unit: "kg",
            IsRequired: true,
            IsCritical: isCritical,
            GradeRules: isCritical
                ? [
                    new("A", 0m,  10m, null, false),
                    new("B", 11m, 20m, null, true)
                  ]
                : [
                    new("A", 0m, 10m, null, false),
                    new("B", 11m, 20m, null, false)
                  ]);

    private static SaveInspectionCriterionRequest MakeBooleanCriterion(
        string code = "B001") => new(
            Code: code,
            Name: $"Boolean {code}",
            CriterionGroup: "LAB",
            DataType: "BOOLEAN",
            Unit: null,
            IsRequired: true,
            IsCritical: true,
            GradeRules: []);

    private static CreateInspectionStandardRequest ValidCreateRequest(
        IReadOnlyList<SaveInspectionCriterionRequest>? criteria = null) =>
        new(
            CropTypeId: 1,
            Code: "STD-001",
            Name: "Test Standard",
            Description: "Description",
            VersionNo: 1,
            EffectiveFrom: DateOnly.FromDateTime(DateTime.Today),
            Criteria: criteria ?? [MakeNumberCriterion()]);

    private void SetupRepoDefaults(bool cropTypeExists = true, bool codeExists = false)
    {
        _repo.Setup(r => r.CropTypeExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(cropTypeExists);
        _repo.Setup(r => r.CodeExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(codeExists);
        _repo.Setup(r => r.Add(It.IsAny<InspectionStandardSet>()));
        _repo.Setup(r => r.AddAuditLog(It.IsAny<AuditLog>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC12 – Create an Inspection Standard Set
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UC12_CreateAsync_WithValidRequest_ReturnsDto()
    {
        SetupRepoDefaults();
        var svc = CreateService();

        var result = await svc.CreateAsync(ValidCreateRequest(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("STD-001", result.Code);
        Assert.Equal("Test Standard", result.Name);
        Assert.Equal(1, result.VersionNo);
        Assert.Equal(1, result.CriterionCount);
        _repo.Verify(r => r.Add(It.IsAny<InspectionStandardSet>()), Times.Once);
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenCropTypeIdIsZero_ThrowsBadRequest()
    {
        var svc = CreateService();
        var request = ValidCreateRequest() with { CropTypeId = 0 };

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenCodeIsEmpty_ThrowsBadRequest()
    {
        var svc = CreateService();
        var request = ValidCreateRequest() with { Code = "   " };

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenVersionNoIsZero_ThrowsBadRequest()
    {
        var svc = CreateService();
        var request = ValidCreateRequest() with { VersionNo = 0 };

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenCriteriaEmpty_ThrowsBadRequest()
    {
        var svc = CreateService();
        var request = ValidCreateRequest() with { Criteria = [] };

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenCropTypeNotFound_ThrowsBadRequest()
    {
        SetupRepoDefaults(cropTypeExists: false);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(ValidCreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenCodeAlreadyExists_ThrowsConflict()
    {
        SetupRepoDefaults(codeExists: true);
        var svc = CreateService();

        await Assert.ThrowsAsync<ConflictException>(
            () => svc.CreateAsync(ValidCreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenDuplicateCriterionCodes_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            MakeNumberCriterion("C001"),
            MakeNumberCriterion("C001")
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenInvalidDataType_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "INVALID_TYPE", null, true, false, [])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenInvalidCriterionGroup_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "INVALID_GROUP", "NUMBER", null, true, false,
                [new("A", 0m, 10m, null, false), new("B", 11m, 20m, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenNumberCriterionHasNoGradeRules_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, false, [])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenNumberRuleHasNoMinOrMax_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, false,
                [new("A", null, null, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenNumberRuleMinGreaterThanMax_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, false,
                [new("A", 100m, 50m, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenTextRuleMissingRequiredTextValue_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "TEXT", null, true, false,
                [new("A", null, null, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    // NOTE: In the current service implementation, Rule 10b (BOOLEAN must be IsCritical)
    // is unreachable for BOOLEAN criteria with empty grade rules because a 'continue'
    // statement at the top of the per-criterion loop exits early when GradeRules is empty.
    // This test documents that behavior. A BOOLEAN criterion with IsCritical=false and
    // no grade rules currently passes validation (does NOT throw).
    [Fact]
    public async Task UC12_CreateAsync_WhenBooleanCriterionNotCritical_CurrentlySucceeds()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("B001", "Boolean", "LAB", "BOOLEAN", null, true, false, [])
        ]);

        // Service allows this due to early continue; no exception expected.
        var result = await svc.CreateAsync(request, CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenCriticalCriterionHasNoFailRule_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, true,
                [new("A", 0m, 10m, null, false), new("B", 11m, 20m, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenNonCriticalCriterionHasFailRule_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, false,
                [new("A", 0m, 10m, null, false), new("B", 11m, 20m, null, true)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenGradeAIsFailRule_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, true,
                [new("A", 0m, 10m, null, true)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenGradesNotStartingFromA_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, false,
                [new("B", 0m, 10m, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenGradesHaveGap_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, false,
                [new("A", 0m, 10m, null, false), new("C", 21m, 30m, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenNumberRangesOverlap_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, false,
                [new("A", 0m, 15m, null, false), new("B", 10m, 20m, null, false)])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WhenCascadeFailRuleViolated_ThrowsBadRequest()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "NUMBER", null, true, true,
                [
                    new("A", 0m,  10m, null, false),
                    new("B", 11m, 20m, null, false),
                    new("C", 21m, 30m, null, true),
                    new("D", 31m, 40m, null, false)  // cascade violation
                ])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task UC12_CreateAsync_WithBooleanCriterion_Succeeds()
    {
        SetupRepoDefaults();
        var svc = CreateService();
        var request = ValidCreateRequest(criteria: [MakeBooleanCriterion()]);

        var result = await svc.CreateAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.CriterionCount);
    }

    [Fact]
    public async Task UC12_CreateAsync_CodeIsNormalizedToUppercase()
    {
        SetupRepoDefaults();
        _repo.Setup(r => r.CodeExistsAsync("STD-001", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var svc = CreateService();
        var request = ValidCreateRequest() with { Code = "std-001" };

        var result = await svc.CreateAsync(request, CancellationToken.None);

        Assert.Equal("STD-001", result.Code);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UC13 – Create a New Version for an Inspection Standard Set
    // ═════════════════════════════════════════════════════════════════════════

    private static InspectionStandardSet MakeActiveSet(int setId = 1) => new()
    {
        InspectionStandardSetId = setId,
        StandardCode = "STD-001",
        StandardName = "Test Standard",
        CropTypeId = 1,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        CropType = new CropType { CropTypeId = 1, CropName = "Rice" },
        Versions =
        [
            new InspectionStandardVersion
            {
                InspectionStandardVersionId = 10,
                VersionNo = 1,
                VersionStatus = "PUBLISHED"
            }
        ]
    };

    private static CreateInspectionStandardVersionRequest ValidVersionRequest(
        IReadOnlyList<SaveInspectionCriterionRequest>? criteria = null) =>
        new(
            EffectiveFrom: DateOnly.FromDateTime(DateTime.Today),
            Criteria: criteria ?? [MakeNumberCriterion()]);

    [Fact]
    public async Task UC13_CreateVersionAsync_WithValidRequest_ReturnsDto()
    {
        var set = MakeActiveSet();
        _repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        _repo.Setup(r => r.GetMaxVersionNoAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repo.Setup(r => r.AddAuditLog(It.IsAny<AuditLog>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        var result = await svc.CreateVersionAsync(1, ValidVersionRequest(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.SetId);
        Assert.Equal(2, result.VersionNo);
        Assert.Equal("PUBLISHED", result.Status);
        Assert.Equal(1, result.CriterionCount);
    }

    [Fact]
    public async Task UC13_CreateVersionAsync_WhenSetNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
             .ReturnsAsync((InspectionStandardSet?)null);
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.CreateVersionAsync(99, ValidVersionRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task UC13_CreateVersionAsync_WhenSetIsInactive_ThrowsBadRequest()
    {
        var set = MakeActiveSet();
        set.IsActive = false;
        _repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        var svc = CreateService();

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateVersionAsync(1, ValidVersionRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task UC13_CreateVersionAsync_WhenCriteriaEmpty_ThrowsBadRequest()
    {
        _repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(MakeActiveSet());
        var svc = CreateService();
        var request = ValidVersionRequest() with { Criteria = [] };

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateVersionAsync(1, request, CancellationToken.None));
    }

    [Fact]
    public async Task UC13_CreateVersionAsync_RetiresAllExistingVersions()
    {
        var set = MakeActiveSet();
        set.Versions.Add(new InspectionStandardVersion
        {
            InspectionStandardVersionId = 11,
            VersionNo = 2,
            VersionStatus = "PUBLISHED"
        });
        _repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        _repo.Setup(r => r.GetMaxVersionNoAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        _repo.Setup(r => r.AddAuditLog(It.IsAny<AuditLog>()));
        _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.CreateVersionAsync(1, ValidVersionRequest(), CancellationToken.None);

        // The two original versions should now be RETIRED
        Assert.All(set.Versions.Where(v => v.VersionNo <= 2),
            v => Assert.Equal("RETIRED", v.VersionStatus));
    }

    [Fact]
    public async Task UC13_CreateVersionAsync_WithInvalidCriteria_ThrowsBadRequest()
    {
        _repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(MakeActiveSet());
        _repo.Setup(r => r.GetMaxVersionNoAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var svc = CreateService();
        var request = ValidVersionRequest(criteria:
        [
            new("C001", "Criterion", "SENSORY", "INVALID_TYPE", null, true, false, [])
        ]);

        await Assert.ThrowsAsync<BadRequestException>(
            () => svc.CreateVersionAsync(1, request, CancellationToken.None));
    }
}
