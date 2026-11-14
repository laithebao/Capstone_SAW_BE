using FluentAssertions;
using SAW.Infrastructure.AiForecast;
using Xunit;

namespace SAW.Test.Infrastructure.AiForecast;

public sealed class ForecastResponseParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithNullOrEmptyText_ShouldReturnFailure(string? rawText)
    {
        var result = ForecastResponseParser.Parse(rawText!, [1]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("rỗng");
        result.Data.Should().BeNull();
    }

    [Fact]
    public void Parse_WithValidForecasts_ShouldMatchResultsByBatchId()
    {
        var result = ForecastResponseParser.Parse("""
            {
              "forecasts": [
                {
                  "productBatchId": 2,
                  "predictedLossPercent": 5.8,
                  "safeStorageRemainingDays": 6,
                  "riskLevel": "MEDIUM",
                  "forecastHorizonDays": 7,
                  "recommendation": "Cần lưu ý dải nhiệt.",
                  "actions": [
                    {"priority":"URGENT","category":"TEMPERATURE","action":"Hạ nhiệt độ kho","reason":"Nhiệt độ cao","deadline":null}
                  ],
                  "analysisNotes": "Chưa có kết quả QC"
                },
                {
                  "productBatchId": 1,
                  "predictedLossPercent": null,
                  "safeStorageRemainingDays": null,
                  "riskLevel": "LOW",
                  "actions": []
                }
              ]
            }
            """, [1, 2]);

        result.IsSuccess.Should().BeTrue();
        var data = result.Data!;
        data[1].RiskLevel.Should().Be("LOW");
        data[1].PredictedLossPercent.Should().BeNull();
        data[1].SafeStorageRemainingDays.Should().BeNull();
        data[2].PredictedLossPercent.Should().Be(5.8m);
        data[2].SafeStorageRemainingDays.Should().Be(6);
        data[2].RiskLevel.Should().Be("MEDIUM");
        data[2].ForecastHorizonDays.Should().Be(7);
        data[2].Recommendation.Should().Be("Cần lưu ý dải nhiệt.");
        data[2].AnalysisNotes.Should().Be("Chưa có kết quả QC");
        var action = data[2].Actions.Should().ContainSingle().Subject;
        action.Priority.Should().Be("URGENT");
        action.Deadline.Should().BeNull();
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"forecasts":[]}""")]
    [InlineData("""{"forecasts":[{"productBatchId":1,"riskLevel":"LOW"}]}""")]
    [InlineData("""{"forecasts":[{"productBatchId":1,"riskLevel":"LOW"},{"productBatchId":1,"riskLevel":"HIGH"}]}""")]
    [InlineData("""{"forecasts":[{"productBatchId":1,"riskLevel":"LOW"},{"productBatchId":3,"riskLevel":"HIGH"}]}""")]
    [InlineData("""{"forecasts":[{"productBatchId":1,"riskLevel":"LOW"},{"productBatchId":2,"riskLevel":"INVALID"}]}""")]
    [InlineData("""{"forecasts":[{"productBatchId":1,"riskLevel":"LOW"},{"productBatchId":2}]}""")]
    public void Parse_WithoutOneValidForecastPerBatch_ShouldRejectRun(string json)
    {
        var result = ForecastResponseParser.Parse(json, [1, 2]);

        result.IsSuccess.Should().BeFalse();
        result.Data.Should().BeNull();
    }

    [Fact]
    public void Parse_WithMarkdownCodeFence_ShouldStripMarkdownAndParse()
    {
        var result = ForecastResponseParser.Parse("""
            Kết quả dự báo
            ```json
            {"forecasts":[{"productBatchId":1,"riskLevel":"MEDIUM","safeStorageRemainingDays":10}]}
            ```
            """, [1]);

        result.IsSuccess.Should().BeTrue();
        result.Data![1].SafeStorageRemainingDays.Should().Be(10);
    }

    [Fact]
    public void Parse_WithEstimatesOutOfRange_ShouldClampEstimates()
    {
        var result = ForecastResponseParser.Parse("""
            {"forecasts":[
                {"productBatchId":1,"riskLevel":"HIGH","predictedLossPercent":-15.5,"safeStorageRemainingDays":-3,"forecastHorizonDays":0},
                {"productBatchId":2,"riskLevel":"CRITICAL","predictedLossPercent":120}
            ]}
            """, [1, 2]);

        result.IsSuccess.Should().BeTrue();
        result.Data![1].PredictedLossPercent.Should().Be(0m);
        result.Data[1].SafeStorageRemainingDays.Should().Be(0);
        result.Data[1].ForecastHorizonDays.Should().Be(1);
        result.Data[2].PredictedLossPercent.Should().Be(100m);
    }

    [Fact]
    public void Parse_WithMalformedJson_ShouldReturnFailureGracefully()
    {
        var result = ForecastResponseParser.Parse("""{"forecasts":[{"productBatchId":1,"recommendation":""", [1]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Lỗi phân tích cú pháp");
    }

    [Fact]
    public void Parse_WithActions_ShouldFilterEmptyActionsAndDefaultUnknownPriority()
    {
        var result = ForecastResponseParser.Parse("""
            {"forecasts":[{
                "productBatchId":1,
                "riskLevel":"HIGH",
                "actions":[
                    {"priority":"NON_STANDARD_PRIORITY","action":"Xuất kho trước"},
                    {"priority":"URGENT","action":"   "}
                ]
            }]}
            """, [1]);

        result.IsSuccess.Should().BeTrue();
        var action = result.Data![1].Actions.Should().ContainSingle().Subject;
        action.Action.Should().Be("Xuất kho trước");
        action.Priority.Should().Be("RECOMMENDED");
    }
}
