using Moq;
using SAW.Application.Features.Traceability.Dtos;
using SAW.Application.Features.Traceability.Services;
using SAW.Application.Repositories;

namespace SAW.Test.Application.Traceability;

public sealed class PublicQcPresentationTests
{
    private static readonly PublicTraceabilityGradeRule[] Rules = [new("A", 0, 1, null, false), new("B", 2, 3, null, false)];
    private static TraceabilityCriterionEvidence Number(decimal? value, string? grade, bool required = true) =>
        new("N", "Tỷ lệ khuyết tật", "SENSORY", "NUMBER", "%", required, new(value, null, null, grade, true), Rules);

    private static async Task<PublicTraceabilityQuality> Present(string grade, params TraceabilityCriterionEvidence[] criteria)
    {
        var token = new string('a', 64);
        var repo = new Mock<ITraceabilityRepository>();
        repo.Setup(r => r.GetByPublicTokenAsync(token, It.IsAny<CancellationToken>())).ReturnsAsync(new TraceabilityData
        {
            IsActive = true, QrImageUrl = "https://example.invalid/qr.png", BatchStatus = "IN_STOCK", BatchQualityGrade = grade,
            LatestQc = new("COMPLETED", "PASS", grade, new(2026, 1, 1), new(2026, 1, 2))
            { Criteria = criteria, Standard = new("TEST", "Tiêu chuẩn thử", 3), SamplingRatio = .1m, SampleSize = 2 }
        });
        return (await new TraceabilityService(repo.Object).GetAsync(token, default)).Quality;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Number_BothBoundariesIncluded_AndStoredGradePreserved(decimal value)
    {
        var quality = await Present("A", Number(value, "A"));
        var c = Assert.Single(quality.Criteria);
        Assert.Equal(value, c.NumericValue); Assert.Equal("%", c.Unit); Assert.Equal("A", c.EvaluatedGrade);
        Assert.Contains("cận dưới và cận trên", c.AssessmentBasis);
        Assert.Equal("Tỷ lệ khuyết tật", Assert.Single(quality.DeterminingCriteria));
        Assert.Equal(10, quality.Sampling!.RatioPercent); Assert.Equal(2, quality.Sampling.SampleWeightKg);
        Assert.Equal(TimeSpan.Zero, quality.CompletedAt.Offset);
    }

    [Fact]
    public async Task Gap_DoesNotPretendValueIsInsideStoredGradesRange()
    {
        var c = Assert.Single((await Present("B", Number(1.5m, "B"))).Criteria);
        Assert.Equal("B", c.EvaluatedGrade);
        Assert.Contains("không nằm trong khoảng nào", c.AssessmentBasis);
        Assert.Contains("cận dưới lớn hơn", c.AssessmentBasis);
        Assert.Equal(2, c.Rules.Single(r => r.Grade == "B").MinValue);
    }

    [Fact]
    public async Task Text_Boolean_OptionalNull_UseActualValuesAndSavedConclusion()
    {
        var text = new TraceabilityCriterionEvidence("T", "Màu sắc", "SENSORY", "TEXT", null, true,
            new(null, "  xanh  ", null, "B", true), [new("B", null, null, "XANH", false)]);
        var boolean = new TraceabilityCriterionEvidence("P", "Kiểm tra bổ sung", "LAB", "BOOLEAN", null, false,
            new(null, null, false, "A", false), []);
        var quality = await Present("B", text, boolean, Number(null, "A", false));
        Assert.Equal("B", quality.Criteria[0].EvaluatedGrade);
        Assert.Contains("không phân biệt", quality.Criteria[0].AssessmentBasis);
        Assert.False(quality.Criteria[1].BooleanValue); Assert.False(quality.Criteria[1].IsPassed);
        Assert.Null(quality.Criteria[1].EvaluatedGrade);
        Assert.False(quality.Criteria[2].HasResult); Assert.Null(quality.Criteria[2].IsPassed);
        Assert.Null(quality.Criteria[2].EvaluatedGrade);
        Assert.Equal("Màu sắc", Assert.Single(quality.DeterminingCriteria));
    }

    [Fact]
    public async Task MissingRequiredOrInconsistentGrades_DoNotInventDeterminingCriterion()
    {
        var missing = await Present("A", Number(null, null));
        Assert.Empty(missing.DeterminingCriteria); Assert.Contains("chưa đủ", missing.GradeExplanation);
        var mismatch = await Present("B", Number(1, "A"));
        Assert.Empty(mismatch.DeterminingCriteria); Assert.Contains("chưa khớp", mismatch.GradeExplanation);
    }

    [Fact]
    public async Task UngradedAndBooleanOnlyResults_ExplainDefaultWithoutClaimingAllAreGradeA()
    {
        var c = new TraceabilityCriterionEvidence("P", "Kiểm tra", "LAB", "BOOLEAN", null, true, new(null, null, true, null, true), []);
        var quality = await Present("A", c);
        Assert.Contains("mặc định", quality.GradeExplanation); Assert.Empty(quality.DeterminingCriteria);
        var outside = await Present("A", Number(100, null));
        Assert.Null(outside.Criteria[0].EvaluatedGrade);
        Assert.Contains("không nằm trong khoảng nào", outside.Criteria[0].AssessmentBasis);
    }
}
