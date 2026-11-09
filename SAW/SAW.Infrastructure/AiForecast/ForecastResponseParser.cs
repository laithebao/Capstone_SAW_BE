using System.Text.Json;
using SAW.Application.Features.SpoilageForecast;

namespace SAW.Infrastructure.AiForecast;

public sealed record ForecastParsedData(
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
    IReadOnlyDictionary<long, ForecastParsedData>? Data,
    string? ErrorMessage
)
{
    public static ForecastParseResult Success(IReadOnlyDictionary<long, ForecastParsedData> data) => new(true, data, null);
    public static ForecastParseResult Failure(string error) => new(false, null, error);
}

public static class ForecastResponseParser
{
    public static ForecastParseResult Parse(string rawText, IEnumerable<long> expectedBatchIds)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return ForecastParseResult.Failure("Phản hồi từ AI rỗng.");

        var cleanJson = ExtractJsonContent(rawText);

        try
        {
            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("forecasts", out var forecasts)
                || forecasts.ValueKind != JsonValueKind.Array)
                return ForecastParseResult.Failure("Thiếu danh sách dự báo.");

            var remainingIds = expectedBatchIds.ToHashSet();
            var results = new Dictionary<long, ForecastParsedData>();
            foreach (var item in forecasts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("productBatchId", out var idEl)
                    || idEl.ValueKind != JsonValueKind.Number
                    || !idEl.TryGetInt64(out var batchId)
                    || !remainingIds.Remove(batchId))
                    return ForecastParseResult.Failure("Kết quả có mã lô không hợp lệ hoặc bị trùng.");

                var riskLevel = item.TryGetProperty("riskLevel", out var levelEl) && levelEl.ValueKind == JsonValueKind.String
                    ? levelEl.GetString()?.Trim().ToUpperInvariant()
                    : null;
                if (riskLevel is not ("LOW" or "MEDIUM" or "HIGH" or "CRITICAL"))
                    return ForecastParseResult.Failure("Thiếu phân loại rủi ro hợp lệ.");

                results.Add(batchId, ParseForecast(item, riskLevel));
            }

            return remainingIds.Count == 0
                ? ForecastParseResult.Success(results)
                : ForecastParseResult.Failure("Thiếu kết quả dự báo cho một số lô.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ForecastParseResult.Failure($"Lỗi phân tích cú pháp kết quả JSON từ AI: {ex.Message}");
        }
    }

    private static ForecastParsedData ParseForecast(JsonElement root, string riskLevel)
    {
        decimal? lossPercent = null;
        if (root.TryGetProperty("predictedLossPercent", out var lossEl) && lossEl.ValueKind == JsonValueKind.Number && lossEl.TryGetDecimal(out var lp))
        {
            lossPercent = Math.Clamp(lp, 0m, 100m);
        }

        int? safeDays = null;
        if (root.TryGetProperty("safeStorageRemainingDays", out var daysEl) && daysEl.ValueKind == JsonValueKind.Number && daysEl.TryGetInt32(out var sd))
        {
            safeDays = Math.Max(0, sd);
        }

        int? horizonDays = 7;
        if (root.TryGetProperty("forecastHorizonDays", out var hEl) && hEl.ValueKind == JsonValueKind.Number && hEl.TryGetInt32(out var hd))
        {
            horizonDays = Math.Max(1, hd);
        }

        var recommendation = "Chưa có nhận định từ hệ thống.";
        if (root.TryGetProperty("recommendation", out var recEl) && recEl.ValueKind == JsonValueKind.String)
        {
            var rec = recEl.GetString()?.Trim();
            if (!string.IsNullOrEmpty(rec)) recommendation = rec;
        }

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

        string? notes = null;
        if (root.TryGetProperty("analysisNotes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
        {
            notes = notesEl.GetString()?.Trim();
        }

        return new ForecastParsedData(
            PredictedLossPercent: lossPercent,
            SafeStorageRemainingDays: safeDays,
            RiskLevel: riskLevel,
            ForecastHorizonDays: horizonDays,
            Recommendation: recommendation,
            Actions: actions,
            AnalysisNotes: notes
        );
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
}
