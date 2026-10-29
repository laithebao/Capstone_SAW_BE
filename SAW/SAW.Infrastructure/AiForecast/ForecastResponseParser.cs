using System.Text.Json;
using SAW.Application.Features.SpoilageForecast;

namespace SAW.Infrastructure.AiForecast;

public sealed record ForecastParsedData(
    decimal SpoilageRiskScore,
    decimal? PredictedLossPercent,
    int? SafeStorageRemainingDays,
    string RiskLevel,
    int? ForecastHorizonDays,
    string Recommendation,
    IReadOnlyList<ForecastActionItem> Actions,
    string? AnalysisNotes
);

public sealed record ForecastParseResult(
    bool IsSuccess,
    ForecastParsedData? Data,
    string? ErrorMessage
)
{
    public static ForecastParseResult Success(ForecastParsedData data) => new(true, data, null);
    public static ForecastParseResult Failure(string error) => new(false, null, error);
}

public static class ForecastResponseParser
{
    public static ForecastParseResult Parse(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return ForecastParseResult.Failure("Phản hồi từ AI rỗng.");

        var cleanJson = ExtractJsonContent(rawText);

        try
        {
            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            // 1. SpoilageRiskScore
            decimal riskScore = 0;
            if (root.TryGetProperty("spoilageRiskScore", out var scoreEl) && scoreEl.TryGetDecimal(out var s))
            {
                riskScore = Math.Clamp(s, 0m, 100m);
            }

            // 2. RiskLevel
            var riskLevel = "MEDIUM";
            if (root.TryGetProperty("riskLevel", out var levelEl) && levelEl.ValueKind == JsonValueKind.String)
            {
                var val = levelEl.GetString()?.Trim().ToUpperInvariant();
                if (val is "LOW" or "MEDIUM" or "HIGH" or "CRITICAL")
                {
                    riskLevel = val;
                }
                else
                {
                    riskLevel = DetermineRiskLevel(riskScore);
                }
            }
            else
            {
                riskLevel = DetermineRiskLevel(riskScore);
            }

            // 3. PredictedLossPercent
            decimal? lossPercent = null;
            if (root.TryGetProperty("predictedLossPercent", out var lossEl) && lossEl.TryGetDecimal(out var lp))
            {
                lossPercent = Math.Clamp(lp, 0m, 100m);
            }

            // 4. SafeStorageRemainingDays
            int? safeDays = null;
            if (root.TryGetProperty("safeStorageRemainingDays", out var daysEl) && daysEl.TryGetInt32(out var sd))
            {
                safeDays = Math.Max(0, sd);
            }

            // 5. ForecastHorizonDays
            int? horizonDays = 7;
            if (root.TryGetProperty("forecastHorizonDays", out var hEl) && hEl.TryGetInt32(out var hd))
            {
                horizonDays = Math.Max(1, hd);
            }

            // 6. Recommendation
            var recommendation = "Chưa có nhận định từ hệ thống.";
            if (root.TryGetProperty("recommendation", out var recEl) && recEl.ValueKind == JsonValueKind.String)
            {
                var rec = recEl.GetString()?.Trim();
                if (!string.IsNullOrEmpty(rec)) recommendation = rec;
            }

            // 7. Actions list
            var actions = new List<ForecastActionItem>();
            if (root.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in actionsEl.EnumerateArray())
                {
                    var priority = item.TryGetProperty("priority", out var pEl) ? pEl.GetString()?.Trim().ToUpperInvariant() ?? "RECOMMENDED" : "RECOMMENDED";
                    if (priority is not ("IMMEDIATE" or "URGENT" or "RECOMMENDED" or "MONITOR"))
                        priority = "RECOMMENDED";

                    var category = item.TryGetProperty("category", out var cEl) ? cEl.GetString()?.Trim().ToUpperInvariant() ?? "OTHER" : "OTHER";
                    var action = item.TryGetProperty("action", out var aEl) ? aEl.GetString()?.Trim() ?? "" : "";
                    var reason = item.TryGetProperty("reason", out var rEl) ? rEl.GetString()?.Trim() ?? "" : "";
                    var deadline = item.TryGetProperty("deadline", out var dEl) ? dEl.GetString()?.Trim() : null;

                    if (!string.IsNullOrWhiteSpace(action))
                    {
                        actions.Add(new ForecastActionItem(priority, category, action, reason, deadline));
                    }
                }
            }

            // 8. AnalysisNotes
            string? notes = null;
            if (root.TryGetProperty("analysisNotes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
            {
                notes = notesEl.GetString()?.Trim();
            }

            return ForecastParseResult.Success(new ForecastParsedData(
                SpoilageRiskScore: riskScore,
                PredictedLossPercent: lossPercent,
                SafeStorageRemainingDays: safeDays,
                RiskLevel: riskLevel,
                ForecastHorizonDays: horizonDays,
                Recommendation: recommendation,
                Actions: actions,
                AnalysisNotes: notes
            ));
        }
        catch (Exception ex)
        {
            return ForecastParseResult.Failure($"Lỗi phân tích cú pháp kết quả JSON từ AI: {ex.Message}");
        }
    }

    private static string ExtractJsonContent(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 7)
            {
                return trimmed[7..end].Trim();
            }
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 3)
            {
                return trimmed[3..end].Trim();
            }
        }

        // Tìm vị trí mở ngoặc { đầu tiên và đóng ngoặc } cuối cùng
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed[firstBrace..(lastBrace + 1)];
        }

        return trimmed;
    }

    private static string DetermineRiskLevel(decimal score) =>
        score switch
        {
            <= 25m => "LOW",
            <= 50m => "MEDIUM",
            <= 75m => "HIGH",
            _ => "CRITICAL"
        };
}
