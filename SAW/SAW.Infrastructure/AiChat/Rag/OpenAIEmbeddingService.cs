using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace SAW.Infrastructure.AiChat.Rag;

public sealed class OpenAIEmbeddingService(
    IHttpClientFactory httpClientFactory,
    OpenAIOptions options,
    ILogger<OpenAIEmbeddingService> logger,
    AiExecutionOptions? execution = null)
{
    internal const string HttpClientName = "OpenAIEmbedding";
    internal const int MaxInputChars = 2000;

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey)
        && !string.IsNullOrWhiteSpace(options.EmbeddingModel)
        && options.EmbeddingDimensions > 0;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        var embeddings = await EmbedBatchAsync([text], ct);
        return embeddings?[0];
    }

    public async Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured || texts.Count == 0 || texts.Any(text =>
                string.IsNullOrWhiteSpace(text) || text.Length > MaxInputChars))
            return null;

        using var http = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            if (execution?.UseMafForChat == true)
                return await EmbedWithSdkAsync(http, texts, ct);

            var body = await OpenAIHttp.PostAsync(http, "embeddings", new
            {
                model = options.EmbeddingModel,
                input = texts,
                dimensions = options.EmbeddingDimensions,
                encoding_format = "float"
            }, options.ApiKey, logger, ct);

            if (body["data"] is JsonArray data && data.Count == texts.Count)
            {
                var embeddings = new float[texts.Count][];
                foreach (var item in data.OfType<JsonObject>())
                {
                    if (item["index"] is not JsonValue indexValue || !indexValue.TryGetValue<int>(out var index)
                        || index < 0 || index >= embeddings.Length || embeddings[index] is not null
                        || item["embedding"] is not JsonArray vector || vector.Count != options.EmbeddingDimensions)
                        break;

                    var values = new float[vector.Count];
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (vector[i] is not JsonValue value || !value.TryGetValue<float>(out values[i])
                            || !float.IsFinite(values[i]))
                            throw new JsonException("Invalid OpenAI embedding value.");
                    }

                    if (values.All(value => value == 0f))
                        break;

                    embeddings[index] = values;
                }

                if (embeddings.All(vector => vector is not null))
                    return embeddings;
            }

            logger.LogWarning("OpenAI trả về batch embedding không đầy đủ hoặc sai chiều.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI embedding quá thời gian chờ.");
        }
        catch (ClientResultException ex)
        {
            logger.LogWarning("OpenAI embedding trả lỗi HTTP {Status}.", ex.Status);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Không thể lấy embedding từ OpenAI.");
        }
        catch (JsonException)
        {
            logger.LogWarning("OpenAI trả embedding không đúng định dạng.");
        }

        return null;
    }

    private async Task<float[][]?> EmbedWithSdkAsync(
        HttpClient http, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var client = MafOpenAI.CreateClient(http, options).GetEmbeddingClient(options.EmbeddingModel);
        var response = await client.GenerateEmbeddingsAsync(texts,
            new EmbeddingGenerationOptions { Dimensions = options.EmbeddingDimensions }, ct);
        var embeddings = new float[texts.Count][];

        if (response.Value.Count != texts.Count)
            return null;

        foreach (var item in response.Value)
        {
            if (item.Index < 0 || item.Index >= embeddings.Length || embeddings[item.Index] is not null)
                return null;

            var vector = item.ToFloats().ToArray();
            if (vector.Length != options.EmbeddingDimensions || !vector.All(float.IsFinite)
                || vector.All(value => value == 0f))
                return null;

            embeddings[item.Index] = vector;
        }

        return embeddings;
    }
}
