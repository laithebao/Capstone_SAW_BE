using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SAW.Infrastructure.AiChat.Rag;

/// <summary>
/// Gọi Gemini Embedding API (text-embedding-004) để chuyển văn bản thành vector 768 chiều.
/// Được đăng ký là Singleton — dùng IHttpClientFactory để tránh captive dependency với HttpClient.
/// </summary>
public sealed class GeminiEmbeddingService(
    IHttpClientFactory httpClientFactory,
    GeminiOptions options,
    ILogger<GeminiEmbeddingService> logger)
{
    /// <summary>Named HttpClient key đã đăng ký trong DI.</summary>
    internal const string HttpClientName = "GeminiEmbedding";

    /// <summary>Thời gian chờ tối thiểu giữa 2 lần gọi embedding (ms) để tránh rate limit.</summary>
    private const int ThrottleDelayMs = 80;

    /// <summary>Giới hạn ký tự input: ~2000 tokens cho tiếng Việt.</summary>
    private const int MaxInputChars = 6000;

    /// <summary>
    /// Tạo embedding vector cho một đoạn văn bản.
    /// </summary>
    /// <returns>Mảng float 768 chiều, hoặc null nếu có lỗi.</returns>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Truncate nếu quá dài
        if (text.Length > MaxInputChars)
            text = text[..MaxInputChars];

        var embeddingModel = string.IsNullOrWhiteSpace(options.EmbeddingModel)
            ? "gemini-embedding-001"
            : options.EmbeddingModel;

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{embeddingModel}:embedContent?key={options.ApiKey}";

        var requestBody = new
        {
            model = $"models/{embeddingModel}",
            content = new
            {
                parts = new[] { new { text } }
            },
            outputDimensionality = 768
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);
            var response = await http.PostAsync(url, httpContent, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("[Embedding] API lỗi {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var values = doc.RootElement
                .GetProperty("embedding")
                .GetProperty("values");

            return values.EnumerateArray()
                .Select(v => v.GetSingle())
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Embedding] Lỗi khi gọi Gemini Embedding API");
            return null;
        }
    }

    /// <summary>
    /// Tạo embedding cho một danh sách text với throttle giữa các lần gọi.
    /// </summary>
    public async IAsyncEnumerable<(string Text, float[]? Embedding)> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var embedding = await EmbedAsync(texts[i], ct);
            yield return (texts[i], embedding);

            // Throttle — tránh rate limit của Gemini API
            if (i < texts.Count - 1)
                await Task.Delay(ThrottleDelayMs, ct);
        }
    }
}
