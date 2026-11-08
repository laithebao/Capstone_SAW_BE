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
        // Act
        var result = ForecastResponseParser.Parse(rawText!);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("rỗng");
        result.Data.Should().BeNull();
    }

    [Fact]
    public void Parse_WithValidPlainJson_ShouldReturnSuccessWithCorrectData()
    {
        // Arrange
        var rawJson = """
        {
          "spoilageRiskScore": 42.5,
          "predictedLossPercent": 5.8,
          "safeStorageRemainingDays": 6,
          "riskLevel": "MEDIUM",
          "forecastHorizonDays": 7,
          "recommendation": "Lô hàng ổn định nhưng cần lưu ý dải nhiệt.",
          "actions": [
            {
              "priority": "URGENT",
              "category": "ENVIRONMENT",
              "action": "Hạ nhiệt độ kho về 4 độ C",
              "reason": "Nhiệt độ hiện tại đang cao hơn ngưỡng",
              "deadline": "Trong 2 giờ"
            }
          ],
          "analysisNotes": "Ghi chú phân tích chi tiết"
        }
        """;

        // Act
        var result = ForecastResponseParser.Parse(rawJson);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.SpoilageRiskScore.Should().Be(42.5m);
        result.Data.PredictedLossPercent.Should().Be(5.8m);
        result.Data.SafeStorageRemainingDays.Should().Be(6);
        result.Data.RiskLevel.Should().Be("MEDIUM");
        result.Data.ForecastHorizonDays.Should().Be(7);
        result.Data.Recommendation.Should().Be("Lô hàng ổn định nhưng cần lưu ý dải nhiệt.");
        result.Data.AnalysisNotes.Should().Be("Ghi chú phân tích chi tiết");
        result.Data.Actions.Should().HaveCount(1);
        result.Data.Actions[0].Action.Should().Be("Hạ nhiệt độ kho về 4 độ C");
        result.Data.Actions[0].Priority.Should().Be("URGENT");
    }

    [Fact]
    public void Parse_WithMarkdownCodeFence_ShouldStripMarkdownAndParse()
    {
        // Arrange
        var markdownText = """
        Dưới đây là kết quả phân tích:
        ```json
        {
          "spoilageRiskScore": 30.0,
          "predictedLossPercent": 3.0,
          "safeStorageRemainingDays": 10,
          "riskLevel": "MEDIUM",
          "forecastHorizonDays": 7,
          "recommendation": "Chất lượng tốt"
        }
        ```
        Xin cảm ơn!
        """;

        // Act
        var result = ForecastResponseParser.Parse(markdownText);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.SpoilageRiskScore.Should().Be(30.0m);
        result.Data.SafeStorageRemainingDays.Should().Be(10);
    }

    [Fact]
    public void Parse_WithScoresOutOfRange_ShouldClampScoresTo0And100()
    {
        // Arrange
        var rawJson = """
        {
          "spoilageRiskScore": 145.0,
          "predictedLossPercent": -15.5,
          "safeStorageRemainingDays": -3,
          "forecastHorizonDays": 0
        }
        """;

        // Act
        var result = ForecastResponseParser.Parse(rawJson);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.SpoilageRiskScore.Should().Be(100m, "Điểm rủi ro phải được clamp tối đa 100");
        result.Data.PredictedLossPercent.Should().Be(0m, "Tỷ lệ tổn thất âm phải được clamp về 0");
        result.Data.SafeStorageRemainingDays.Should().Be(0, "Số ngày bảo quản âm phải được clamp về 0");
        result.Data.ForecastHorizonDays.Should().Be(1, "Khung dự báo nhỏ hơn 1 phải được đưa về tối thiểu 1");
    }

    [Theory]
    [InlineData(15.0, "LOW")]
    [InlineData(25.0, "LOW")]
    [InlineData(35.0, "MEDIUM")]
    [InlineData(50.0, "MEDIUM")]
    [InlineData(65.0, "HIGH")]
    [InlineData(75.0, "HIGH")]
    [InlineData(85.0, "CRITICAL")]
    [InlineData(99.0, "CRITICAL")]
    public void Parse_WhenRiskLevelIsMissingOrInvalid_ShouldAutoDetermineFromScore(decimal score, string expectedLevel)
    {
        // Arrange: AI trả về trường riskLevel không hợp lệ hoặc không có trong enum
        var rawJson = $$"""
        {
          "spoilageRiskScore": {{score}},
          "riskLevel": "UNKNOWN_CUSTOM_LEVEL"
        }
        """;

        // Act
        var result = ForecastResponseParser.Parse(rawJson);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data!.RiskLevel.Should().Be(expectedLevel);
    }

    [Fact]
    public void Parse_WithMalformedJson_ShouldReturnFailureGracefullyWithoutException()
    {
        // Arrange
        var badJson = "{ \"spoilageRiskScore\": 50, \"recommendation\": ";

        // Act
        var result = ForecastResponseParser.Parse(badJson);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Lỗi phân tích cú pháp");
        result.Data.Should().BeNull();
    }

    [Fact]
    public void Parse_WithActions_ShouldFilterEmptyActionsAndDefaultUnknownPriority()
    {
        // Arrange
        var rawJson = """
        {
          "spoilageRiskScore": 55.0,
          "actions": [
            {
              "priority": "NON_STANDARD_PRIORITY",
              "category": "DISPATCH",
              "action": "Xuất kho trước",
              "reason": "Hạn dùng sắp tới"
            },
            {
              "priority": "URGENT",
              "category": "OTHER",
              "action": "   ",
              "reason": "Hành động trống"
            }
          ]
        }
        """;

        // Act
        var result = ForecastResponseParser.Parse(rawJson);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data!.Actions.Should().HaveCount(1, "Hành động có trường action rỗng phải bị loại bỏ");
        result.Data.Actions[0].Action.Should().Be("Xuất kho trước");
        result.Data.Actions[0].Priority.Should().Be("RECOMMENDED", "Priority lạ phải tự fallback về RECOMMENDED");
    }
}
