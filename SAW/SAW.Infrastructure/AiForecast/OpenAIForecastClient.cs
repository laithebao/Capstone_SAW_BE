using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SAW.Infrastructure.AiChat;

namespace SAW.Infrastructure.AiForecast;

public sealed class OpenAIForecastClient(HttpClient http, OpenAIOptions options, ILogger<OpenAIForecastClient> logger) : IForecastModelClient
{
    public async Task<(string Json, string Model)> GenerateAsync(
        string instructions, string input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model)
            || options.ForecastMaxOutputTokens <= 0)
        {
            logger.LogWarning("OpenAI dự báo hao hụt chưa được cấu hình đầy đủ.");
            throw new HttpRequestException("OpenAI forecast is not configured.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ForecastTimeoutSeconds, 1, 100)));

        var payload = new JsonObject
        {
            ["model"] = options.Model,
            ["instructions"] = instructions,
            ["input"] = input,
            ["max_output_tokens"] = options.ForecastMaxOutputTokens,
            ["store"] = false,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "spoilage_forecast",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(ForecastOutputSchema.Value.GetRawText())
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            payload["reasoning"] = new JsonObject { ["effort"] = options.ReasoningEffort };

        var response = await OpenAIHttp.PostAsync(http, "responses", payload, options.ApiKey, logger, deadline.Token);
        if (OpenAIHttp.ReadString(response["status"]) != "completed" || response["output"] is not JsonArray output)
            throw new JsonException("OpenAI forecast did not complete.");

        var parts = OpenAIHttp.ReadMessageParts(output).ToArray();
        if (parts.Any(part => OpenAIHttp.ReadString(part["type"]) == "refusal"))
            throw new JsonException("OpenAI refused to generate a forecast.");

        var json = string.Concat(parts
            .Where(part => OpenAIHttp.ReadString(part["type"]) == "output_text")
            .Select(part => OpenAIHttp.ReadString(part["text"])));

        if (string.IsNullOrWhiteSpace(json))
            throw new JsonException("OpenAI forecast output is empty.");

        var model = OpenAIHttp.ReadString(response["model"]);
        return (json, string.IsNullOrWhiteSpace(model) ? options.Model : model);
    }
}
