using Microsoft.Extensions.Logging;
using SAW.Infrastructure.AiChat.Rag;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Builds and searches a per-process Knowledge Base index using OpenAI embeddings.
/// </summary>
public sealed class KnowledgeBaseService(
    OpenAIEmbeddingService embedder,
    IEnumerable<IDocumentLoader> loaders,
    AiChatOptions options,
    ILogger<KnowledgeBaseService> logger)
{
    private const int TopK = 4;
    private const int EmbeddingBatchSize = 16;
    private static readonly TimeSpan RebuildCooldown = TimeSpan.FromSeconds(30);
    private const string Unavailable = "Kho tri thức đang khởi tạo hoặc tạm thời gián đoạn. Vui lòng thử lại sau.";

    private volatile IReadOnlyList<KnowledgeChunk>? _index;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private DateTimeOffset _nextBuildAt;

    public bool IsReady => _index is not null;

    public (bool IsReady, int ChunkCount, IReadOnlyList<string> IndexedFiles) GetStatus()
    {
        var index = _index;
        return (index is not null, index?.Count ?? 0, index?.Select(c => c.SourceFile).Distinct().ToArray() ?? []);
    }

    public async Task<string> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Vui lòng cung cấp nội dung cần tìm kiếm.";

        var index = _index;
        if (index is null)
        {
            await BuildIndexAsync(ct);
            index = _index;
            if (index is null)
                return Unavailable;
        }

        var queryVec = await embedder.EmbedAsync(query, ct);
        if (queryVec is null)
            return Unavailable;

        var topResults = index
            .Select(chunk => (chunk, score: CosineSimilarity(queryVec, chunk.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(TopK)
            .ToArray();

        logger.LogDebug("Knowledge Base top cosine score: {Score:F3}.", topResults[0].score);

        if (topResults[0].score < options.KnowledgeBaseMinTopScore)
            return "Kho tri thức không có tài liệu liên quan trực tiếp đến câu hỏi.";

        var relevant = topResults.Where(r => r.score >= options.KnowledgeBaseMinChunkScore)
            .Select(r => r.chunk.Content).ToArray();
        return relevant.Length > 0
            ? string.Join("\n\n---\n\n", relevant)
            : topResults[0].chunk.Content;
    }

    public async Task<bool> BuildIndexAsync(CancellationToken ct = default)
    {
        if (!embedder.IsConfigured || !await _buildLock.WaitAsync(0, ct))
            return false;

        try
        {
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
                .ToDictionary(x => x.Extension, x => x.Loader, StringComparer.OrdinalIgnoreCase);

            var files = Directory.EnumerateFiles(resolvedPath, "*", SearchOption.AllDirectories)
                .Where(file => loaderMap.ContainsKey(Path.GetExtension(file)))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();

            var newIndex = new List<KnowledgeChunk>();
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                var text = await loaderMap[Path.GetExtension(path)].ExtractTextAsync(path, ct);
                var chunks = DocumentChunker.Chunk(text).ToArray();
                if (chunks.Length == 0)
                {
                    logger.LogWarning("Knowledge Base không trích xuất được nội dung từ {File}.", fileName);
                    return false;
                }

                var chunkIndex = 0;
                foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
                {
                    var embeddings = await embedder.EmbedBatchAsync(batch, ct);
                    if (embeddings is null)
                    {
                        logger.LogWarning("Knowledge Base không tạo được embedding cho {File}.", fileName);
                        return false;
                    }

                    for (var i = 0; i < batch.Length; i++)
                        newIndex.Add(new KnowledgeChunk(embeddings[i], batch[i], fileName, chunkIndex++));
                }
            }

            ct.ThrowIfCancellationRequested();
            if (newIndex.Count == 0)
            {
                logger.LogWarning("Không có tài liệu Knowledge Base để lập chỉ mục.");
                return false;
            }

            // Both query and index embeddings use the same immutable provider configuration.
            _index = newIndex;
            logger.LogInformation("Knowledge Base đã lập chỉ mục {ChunkCount} chunks từ {FileCount} tài liệu.",
                newIndex.Count, files.Length);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Knowledge Base không lập được chỉ mục ({ErrorType}).", ex.GetType().Name);
            return false;
        }
        finally
        {
            _buildLock.Release();
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

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return -1;

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            magA += (double)a[i] * a[i];
            magB += (double)b[i] * b[i];
        }

        return dot / Math.Sqrt(magA * magB);
    }
}
