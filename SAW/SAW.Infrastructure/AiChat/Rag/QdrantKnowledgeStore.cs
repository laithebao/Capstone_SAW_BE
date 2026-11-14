using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAW.Infrastructure.AiChat.Rag;

public sealed class QdrantKnowledgeStore(IHttpClientFactory httpFactory, QdrantOptions options)
{
    internal const string HttpClientName = "QdrantKnowledgeBase";
    private const string ProfileField = "embedding_profile";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal bool IsConfigured => options.IsConfigured;
    internal string CollectionAlias => options.CollectionAlias;

    internal async Task<string?> GetActiveCollectionAsync(CancellationToken ct)
    {
        var result = await SendAsync<AliasList>(HttpMethod.Get, "aliases", null, ct);
        var aliases = result.Aliases ?? throw new JsonException("Qdrant response has no alias list.");
        return aliases.FirstOrDefault(alias => alias.AliasName == CollectionAlias)?.CollectionName;
    }

    internal async Task CreateCollectionAsync(string collection, int dimensions, CancellationToken ct)
    {
        var path = CollectionPath(collection);
        await SendWriteAsync(HttpMethod.Put, path,
            new { vectors = new { size = dimensions, distance = "Cosine" } }, ct);
        // Qdrant Cloud requires an index on every payload field used in a retrieval filter.
        await SendCompletedWriteAsync($"{path}/index?wait=true",
            new { fieldName = ProfileField, fieldSchema = "keyword" }, ct);
    }

    internal Task UploadAsync(string collection, KnowledgeChunk[] chunks, float[][] vectors,
        int startIndex, string profile, CancellationToken ct)
    {
        var points = chunks.Select((chunk, index) => new
        {
            id = startIndex + index,
            vector = vectors[index],
            payload = new
            {
                chunk.Content,
                chunk.SourceFile,
                chunk.ChunkIndex,
                embeddingProfile = profile
            }
        }).ToArray();

        return SendCompletedWriteAsync(
            $"{CollectionPath(collection)}/points?wait=true", new { points }, ct);
    }

    internal Task ActivateAsync(string collection, bool replaceExisting, CancellationToken ct)
    {
        var create = new { createAlias = new { collectionName = collection, aliasName = CollectionAlias } };
        object[] actions = replaceExisting
            ? [new { deleteAlias = new { aliasName = CollectionAlias } }, create]
            : [create];
        return SendWriteAsync(HttpMethod.Post, "collections/aliases", new { actions }, ct);
    }

    internal Task DeleteCollectionAsync(string collection, CancellationToken ct) =>
        SendWriteAsync(HttpMethod.Delete, CollectionPath(collection), null, ct);

    internal async Task<(float Score, string Content)[]> SearchAsync(float[] vector, int limit,
        string profile, CancellationToken ct)
    {
        var result = await SendAsync<QueryResult>(HttpMethod.Post, $"{CollectionPath(CollectionAlias)}/points/query",
            new
            {
                query = vector,
                filter = ProfileFilter(profile),
                @params = new { exact = true },
                limit,
                withPayload = new[] { "content" }
            }, ct);

        var points = result.Points ?? throw new JsonException("Qdrant response has no search results.");
        return points.Select(point => (point.Score,
            point.Payload?.Content ?? throw new JsonException("Qdrant result has no chunk content."))).ToArray();
    }

    internal async Task<(bool IsReady, int ChunkCount, IReadOnlyList<string> IndexedFiles)> GetStatusAsync(
        string profile, CancellationToken ct)
    {
        var collection = await GetActiveCollectionAsync(ct);
        if (collection is null)
            return (false, 0, []);

        var files = new SortedSet<string>(StringComparer.Ordinal);
        var count = 0;
        JsonElement? offset = null;
        do
        {
            var page = await SendAsync<ScrollResult>(HttpMethod.Post, $"{CollectionPath(collection)}/points/scroll",
                new { filter = ProfileFilter(profile), limit = 256, offset, withPayload = new[] { "source_file" } }, ct);
            var points = page.Points ?? throw new JsonException("Qdrant response has no page of points.");
            foreach (var point in points)
                files.Add(point.Payload?.SourceFile ?? throw new JsonException("Qdrant result has no source file."));

            count += points.Length;
            offset = page.NextPageOffset;
        } while (offset.HasValue);

        return (count > 0, count, files.ToArray());
    }

    private async Task SendWriteAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!await SendAsync<bool>(method, path, body, ct))
            throw new JsonException("Qdrant did not confirm the write.");
    }

    private async Task SendCompletedWriteAsync(string path, object body, CancellationToken ct)
    {
        var result = await SendAsync<WriteResult>(HttpMethod.Put, path, body, ct);
        if (result.Status != "completed")
            throw new JsonException("Qdrant did not confirm the write was completed.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<QdrantResponse<T>>(JsonOptions, ct);
        return envelope is { Result: not null } && envelope.Status == "ok"
            ? envelope.Result
            : throw new JsonException("Invalid Qdrant response.");
    }

    private static string CollectionPath(string collection) => $"collections/{Uri.EscapeDataString(collection)}";

    private static object ProfileFilter(string profile) =>
        new { must = new[] { new { key = ProfileField, match = new { value = profile } } } };

    private sealed record QdrantResponse<T>(string Status, [property: JsonRequired] T Result);
    private sealed record AliasList(QdrantAlias[]? Aliases);
    private sealed record QdrantAlias(string AliasName, string CollectionName);
    private sealed record WriteResult(string Status);
    private sealed record QueryResult(ScoredPoint[]? Points);
    private sealed record ScrollResult(StoredPoint[]? Points, JsonElement? NextPageOffset);
    private sealed record ScoredPoint([property: JsonRequired] float Score, PointPayload? Payload);
    private sealed record StoredPoint(PointPayload? Payload);
    private sealed record PointPayload(string? Content, string? SourceFile);
}
