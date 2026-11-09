using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAW.Infrastructure.AiChat.Rag;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Publishes and searches the shared Qdrant index using OpenAI embeddings.
/// </summary>
public sealed class KnowledgeBaseService(
    OpenAIEmbeddingService embedder,
    IEnumerable<IDocumentLoader> loaders,
    AiChatOptions options,
    OpenAIOptions openAIOptions,
    QdrantKnowledgeStore store,
    ILogger<KnowledgeBaseService> logger)
{
    private const int TopK = 4;
    private const int EmbeddingBatchSize = 16;
    private static readonly TimeSpan RebuildCooldown = TimeSpan.FromSeconds(30);
    private const string Unavailable = "Kho tri thức đang khởi tạo hoặc tạm thời gián đoạn. Vui lòng thử lại sau.";

    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private DateTimeOffset _nextBuildAt;

    internal bool IsConfigured => embedder.IsConfigured && store.IsConfigured;
    private string EmbeddingProfile =>
        FormattableString.Invariant($"{openAIOptions.EmbeddingModel}:{openAIOptions.EmbeddingDimensions}");

    public async Task<(bool IsReady, int ChunkCount, IReadOnlyList<string> IndexedFiles)> GetStatusAsync(
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return (false, 0, []);

        try
        {
            return await store.GetStatusAsync(EmbeddingProfile, ct);
        }
        catch (Exception ex) when (IsStoreFailure(ex, ct))
        {
            LogStoreFailure(ex);
            return (false, 0, []);
        }
    }

    public async Task<string> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Vui lòng cung cấp nội dung cần tìm kiếm.";

        if (!IsConfigured)
            return Unavailable;

        var queryVec = await embedder.EmbedAsync(query, ct);
        if (queryVec is null)
            return Unavailable;

        try
        {
            var topResults = await store.SearchAsync(queryVec, TopK, EmbeddingProfile, ct);
            if (topResults.Length == 0)
                return Unavailable;

            logger.LogDebug("Knowledge Base top cosine score: {Score:F3}.", topResults[0].Score);
            if (topResults[0].Score < options.KnowledgeBaseMinTopScore)
                return "Kho tri thức không có tài liệu liên quan trực tiếp đến câu hỏi.";

            var relevant = topResults.Where(result => result.Score >= options.KnowledgeBaseMinChunkScore)
                .Select(result => result.Content).ToArray();
            return relevant.Length > 0
                ? string.Join("\n\n---\n\n", relevant)
                : topResults[0].Content;
        }
        catch (Exception ex) when (IsStoreFailure(ex, ct))
        {
            LogStoreFailure(ex);
            return Unavailable;
        }
    }

    internal Task<bool> EnsureIndexAsync(CancellationToken ct) =>
        BuildIndexAsync(initializeOnly: true, ct);

    public Task<bool> BuildIndexAsync(CancellationToken ct = default) =>
        BuildIndexAsync(initializeOnly: false, ct);

    private async Task<bool> BuildIndexAsync(bool initializeOnly, CancellationToken ct)
    {
        if (!IsConfigured || !await _buildLock.WaitAsync(0, ct))
            return false;

        string? unpublishedCollection = null;
        try
        {
            var activeCollection = await store.GetActiveCollectionAsync(ct);
            // A running deployment owns the alias until an explicit reindex publishes an update.
            if (initializeOnly && activeCollection is not null)
                return true;

            if (DateTimeOffset.UtcNow < _nextBuildAt)
                return false;

            // Throttle attempts, including failures and manual rebuilds.
            _nextBuildAt = DateTimeOffset.UtcNow + RebuildCooldown;
            var resolvedPath = ResolvePath(options.KnowledgeBasePath);
            if (!Directory.Exists(resolvedPath))
            {
                logger.LogWarning("Không tìm thấy thư mục Knowledge Base.");
                return false;
            }

            var loaderMap = loaders
                .SelectMany(loader => loader.SupportedExtensions.Select(ext => (Extension: ext, Loader: loader)))
                .ToDictionary(entry => entry.Extension, entry => entry.Loader, StringComparer.OrdinalIgnoreCase);

            var files = Directory.EnumerateFiles(resolvedPath, "*", SearchOption.AllDirectories)
                .Where(file => loaderMap.ContainsKey(Path.GetExtension(file)))
                .Select(path => (Path: path,
                    Name: Path.GetRelativePath(resolvedPath, path).Replace(Path.DirectorySeparatorChar, '/')))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();

            var allChunks = new List<KnowledgeChunk>();
            foreach (var (path, fileName) in files)
            {
                ct.ThrowIfCancellationRequested();
                var text = await loaderMap[Path.GetExtension(path)].ExtractTextAsync(path, ct);
                var chunks = DocumentChunker.Chunk(text).ToArray();
                if (chunks.Length == 0)
                {
                    logger.LogWarning("Knowledge Base không trích xuất được nội dung từ {File}.", fileName);
                    return false;
                }

                allChunks.AddRange(chunks.Select((chunk, index) => new KnowledgeChunk(chunk, fileName, index)));
            }

            ct.ThrowIfCancellationRequested();
            if (allChunks.Count == 0)
            {
                logger.LogWarning("Không có tài liệu Knowledge Base để lập chỉ mục.");
                return false;
            }

            var model = openAIOptions.EmbeddingModel;
            var dimensions = openAIOptions.EmbeddingDimensions;
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { schemaVersion = 1, model, dimensions, chunks = allChunks })));
            var versionPrefix = $"{store.CollectionAlias}-{fingerprint[..24].ToLowerInvariant()}-";
            if (activeCollection?.StartsWith(versionPrefix, StringComparison.Ordinal) == true)
                return true;

            unpublishedCollection = $"{versionPrefix}{Guid.NewGuid():N}";
            await store.CreateCollectionAsync(unpublishedCollection, dimensions, ct);
            var startIndex = 0;
            foreach (var batch in allChunks.Chunk(EmbeddingBatchSize))
            {
                var embeddings = await embedder.EmbedBatchAsync(batch.Select(chunk => chunk.Content).ToArray(), ct);
                if (embeddings is null)
                    return false;

                await store.UploadAsync(unpublishedCollection, batch, embeddings, startIndex, EmbeddingProfile, ct);
                startIndex += batch.Length;
            }

            activeCollection = await store.GetActiveCollectionAsync(ct);
            if ((initializeOnly && activeCollection is not null) ||
                activeCollection?.StartsWith(versionPrefix, StringComparison.Ordinal) == true)
                return true;

            var collection = unpublishedCollection;
            // A timeout during the alias update may still mean it was applied.
            unpublishedCollection = null;
            await store.ActivateAsync(collection, activeCollection is not null, ct);
            logger.LogInformation("Knowledge Base đã lập chỉ mục {ChunkCount} chunks từ {FileCount} tài liệu.",
                allChunks.Count, files.Length);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Knowledge Base không lập được chỉ mục ({ErrorType}, HTTP {StatusCode}).",
                ex.GetType().Name, (ex as HttpRequestException)?.StatusCode);
            return false;
        }
        finally
        {
            try
            {
                if (unpublishedCollection is not null)
                    await DeleteDraftAsync(unpublishedCollection);
            }
            finally
            {
                _buildLock.Release();
            }
        }
    }

    private static string ResolvePath(string path)
    {
        if (path == "KnowledgeBase")
        {
            var sourcePath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "../../../../SAW.Infrastructure/AiChat/KnowledgeBase"));
            if (Directory.Exists(sourcePath))
                return sourcePath;
        }

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    private async Task DeleteDraftAsync(string collection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await store.DeleteCollectionAsync(collection, timeout.Token);
        }
        catch (Exception ex) when (IsStoreFailure(ex, CancellationToken.None))
        {
            logger.LogWarning("Không thể dọn collection Qdrant chưa công bố {Collection} ({ErrorType}).",
                collection, ex.GetType().Name);
        }
    }

    private static bool IsStoreFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException ||
        ex is OperationCanceledException && !ct.IsCancellationRequested;

    private void LogStoreFailure(Exception ex) =>
        logger.LogWarning("Không truy cập được Qdrant Knowledge Base ({ErrorType}, HTTP {StatusCode}).",
            ex.GetType().Name, (ex as HttpRequestException)?.StatusCode);
}
