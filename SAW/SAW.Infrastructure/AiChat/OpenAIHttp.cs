using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SAW.Infrastructure.AiChat;

internal static class OpenAIHttp
{
    internal static readonly Uri BaseAddress = new("https://api.openai.com/v1/");

    internal static async Task<JsonObject> PostAsync(
        HttpClient http,
        string path,
        object payload,
        string apiKey,
        ILogger logger,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex) when (attempt == 1 && ex.StatusCode is null)
            {
                logger.LogWarning("OpenAI {Endpoint}: lỗi mạng, thử lại một lần.", path);
                await Task.Delay(RetryDelay(null), ct);
                continue;
            }

            using (response)
            {
                response.Headers.TryGetValues("x-request-id", out var requestIds);
                var requestId = requestIds?.FirstOrDefault();

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                    logger.LogDebug("OpenAI {Endpoint}: request {RequestId}, usage {Usage}.",
                        path, requestId, body?["usage"]);
                    return body ?? throw new JsonException("Missing OpenAI response object.");
                }

                logger.LogWarning("OpenAI {Endpoint}: HTTP {Status}, request {RequestId}, attempt {Attempt}.",
                    path, (int)response.StatusCode, requestId, attempt);

                var retryable = (int)response.StatusCode >= 500
                    || (response.StatusCode == HttpStatusCode.TooManyRequests && !await IsQuotaErrorAsync(response, ct));
                var retryAfter = response.Headers.RetryAfter;
                var delay = retryAfter?.Delta ?? retryAfter?.Date - DateTimeOffset.UtcNow;

                if (attempt == 1 && retryable && (delay is null || delay <= TimeSpan.FromSeconds(5)))
                {
                    await Task.Delay(RetryDelay(delay), ct);
                    continue;
                }

                throw new HttpRequestException("OpenAI request failed.", null, response.StatusCode);
            }
        }
    }

    internal static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static IEnumerable<JsonObject> ReadMessageParts(JsonArray output) =>
        output.OfType<JsonObject>()
            .Where(item => ReadString(item["type"]) == "message")
            .SelectMany(item => (item["content"] as JsonArray ?? []).OfType<JsonObject>());

    private static TimeSpan RetryDelay(TimeSpan? retryAfter) =>
        TimeSpan.FromMilliseconds(Math.Max(250, retryAfter?.TotalMilliseconds ?? 0) + Random.Shared.Next(100, 350));

    private static async Task<bool> IsQuotaErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            var error = body?["error"] as JsonObject;
            return IsQuotaCode(error?["code"]) || IsQuotaCode(error?["type"]);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsQuotaCode(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var code)
        && code is "insufficient_quota" or "billing_hard_limit_reached";
}
