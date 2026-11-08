using Microsoft.Extensions.Logging;
using SAW.Infrastructure.AiChat.Rag;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// RAG Knowledge Base Service — triển khai Retrieval-Augmented Generation chuẩn:
/// 1. Đọc tài liệu từ thư mục (hỗ trợ .md, .txt, .pdf, .docx)
/// 2. Chia thành chunks với overlap
/// 3. Embed từng chunk bằng Gemini text-embedding-004
/// 4. Lưu index trong memory (vector store)
/// 5. Khi search: embed query → cosine similarity → trả về top-K chunks liên quan
/// 
/// Index được xây dựng bởi <see cref="KnowledgeBaseIndexingService"/> khi server khởi động.
/// </summary>
public sealed class KnowledgeBaseService(
    GeminiEmbeddingService embedder,
    IEnumerable<IDocumentLoader> loaders,
    GeminiOptions options,
    ILogger<KnowledgeBaseService> logger)
{
    // ── Vector index ──────────────────────────────────────────────────────
    private volatile List<KnowledgeChunk> _index = [];
    private volatile bool _indexReady;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    // Top-K chunks trả về mỗi lần search
    private const int TopK = 4;

    /// <summary>Số lượng chunk hiện có trong vector index.</summary>
    public int ChunkCount => _index.Count;

    /// <summary>Index đã sẵn sàng chưa.</summary>
    public bool IsReady => _indexReady;

    /// <summary>Danh sách tên các file đã được lập chỉ mục.</summary>
    public IReadOnlyList<string> IndexedFiles => _index.Select(c => c.SourceFile).Distinct().ToList();

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Tìm kiếm ngữ nghĩa (semantic search) trong Knowledge Base.
    /// Tự động nạp index nếu chưa sẵn sàng hoặc rỗng (self-healing / lazy load).
    /// </summary>
    public async Task<string> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Vui lòng cung cấp nội dung cần tìm kiếm.";

        // Tự động xây dựng index nếu chưa sẵn sàng hoặc rỗng (self-healing / lazy load)
        if (!_indexReady || _index.Count == 0)
        {
            await _buildLock.WaitAsync(ct);
            try
            {
                if (!_indexReady || _index.Count == 0)
                {
                    logger.LogInformation("[KnowledgeBase] Index chưa sẵn sàng hoặc rỗng ({Count} chunks). Đang tự động xây dựng lại...", _index.Count);
                    await BuildIndexAsync(ct);
                }
            }
            finally
            {
                _buildLock.Release();
            }
        }

        if (_index.Count == 0)
            return "Kho tri thức chưa có tài liệu. Vui lòng liên hệ quản trị viên để thêm tài liệu vào thư mục KnowledgeBase.";

        // 1. Embed query
        var queryVec = await embedder.EmbedAsync(query, ct);
        if (queryVec is null)
            return "Không thể truy cập kho tri thức lúc này. Vui lòng thử lại.";

        // 2. Cosine similarity với toàn bộ index → lấy top-K
        var topResults = _index
            .Select(chunk => (chunk, score: CosineSimilarity(queryVec, chunk.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(TopK)
            .ToList();

        // 3. Log để debug — xem score thực tế trong server log
        logger.LogInformation(
            "[KnowledgeBase] Query: '{Query}' → top scores: {Scores}",
            query,
            string.Join(", ", topResults.Select(r => $"{r.chunk.SourceFile}={r.score:F3}")));

        // 4. Nếu điểm số cao nhất quá thấp (< 0.28f), không có tài liệu nào khớp
        if (topResults.Count == 0 || topResults[0].score < 0.28f)
        {
            logger.LogInformation(
                "[KnowledgeBase] Query '{Query}' không khớp tài liệu trong Knowledge Base (top score = {Score:F3} < 0.28)",
                query, topResults.FirstOrDefault().score);

            return $"Không tìm thấy tài liệu nào trong Knowledge Base liên quan trực tiếp đến '{query}'.\n" +
                   "(Lưu ý dành cho AI: Nếu câu hỏi là về kiến thức nông nghiệp, tiêu chuẩn kiểm định (VietGAP, GlobalGAP...) " +
                   "hay kỹ thuật nông sản nói chung mà tài liệu trên chưa có, bạn hãy sử dụng kiến thức chuyên môn nông nghiệp của mình để giải thích chi tiết cho người dùng).";
        }

        // 5. Trả các chunks có độ liên quan hợp lý (>= 0.25f)
        var relevantResults = topResults.Where(r => r.score >= 0.25f).ToList();
        if (relevantResults.Count == 0)
            relevantResults = topResults.Take(1).ToList();

        var contents = relevantResults.Select(r => r.chunk.Content).ToList();
        return string.Join("\n\n---\n\n", contents);
    }


    // ── Index building (called by KnowledgeBaseIndexingService) ──────────

    /// <summary>
    /// Xây dựng vector index từ toàn bộ tài liệu trong thư mục KnowledgeBase.
    /// Được gọi bởi <see cref="KnowledgeBaseIndexingService"/> ở background khi server khởi động.
    /// </summary>
    public async Task BuildIndexAsync(CancellationToken ct = default)
    {
        var resolvedPath = ResolvePath(options.KnowledgeBasePath);

        // Khi chạy local, ưu tiên thư mục tài liệu từ source code để luôn nạp file mới thêm (.pdf, .docx, .md) ngay lập tức
        var devSourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SAW.Infrastructure/AiChat/KnowledgeBase"));
        if (Directory.Exists(devSourcePath))
        {
            logger.LogInformation("[KnowledgeBase] Chạy môi trường local: sử dụng thư mục tài liệu từ source code: {Path}", devSourcePath);
            resolvedPath = devSourcePath;
        }

        if (!Directory.Exists(resolvedPath))
        {
            logger.LogWarning("[KnowledgeBase] Thư mục không tồn tại: {Path}. " +
                "Tạo thư mục và thêm tài liệu (.md, .txt, .pdf, .docx) để bật tính năng này.", resolvedPath);
            _indexReady = true;
            return;
        }

        // Xây dựng map extension → loader
        var loaderMap = loaders
            .SelectMany(l => l.SupportedExtensions
                .Select(ext => (Ext: ext.ToLowerInvariant(), Loader: l)))
            .ToDictionary(x => x.Ext, x => x.Loader);

        var supportedExtensions = string.Join(", ", loaderMap.Keys);
        var allFiles = Directory.GetFiles(resolvedPath, "*.*", SearchOption.AllDirectories)
            .Where(f => loaderMap.ContainsKey(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        logger.LogInformation("[KnowledgeBase] Bắt đầu lập chỉ mục {FileCount} tài liệu ({Extensions})...",
            allFiles.Count, supportedExtensions);

        var newIndex = new List<KnowledgeChunk>();

        foreach (var filePath in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var loader = loaderMap[ext];
            var fileName = Path.GetFileName(filePath);

            try
            {
                // Bước 1: Trích xuất text
                var rawText = await loader.ExtractTextAsync(filePath, ct);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    logger.LogWarning("[KnowledgeBase] File rỗng hoặc không trích xuất được text: {File}", fileName);
                    continue;
                }

                // Bước 2: Chunk
                var chunks = DocumentChunker.Chunk(rawText).ToList();
                logger.LogDebug("[KnowledgeBase] {File}: {Chars} ký tự → {Chunks} chunks",
                    fileName, rawText.Length, chunks.Count);

                // Bước 3: Embed từng chunk
                int chunkIdx = 0;
                await foreach (var (_, embedding) in embedder.EmbedBatchAsync(chunks, ct))
                {
                    if (embedding is not null)
                        newIndex.Add(new KnowledgeChunk(embedding, chunks[chunkIdx], fileName, chunkIdx));
                    else
                        logger.LogWarning("[KnowledgeBase] Không embed được chunk {Idx} của {File}", chunkIdx, fileName);
                    chunkIdx++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[KnowledgeBase] Lỗi khi xử lý file: {File}", fileName);
            }
        }

        _index = newIndex;
        _indexReady = true;

        logger.LogInformation("[KnowledgeBase] Lập chỉ mục hoàn tất: {ChunkCount} chunks từ {FileCount} tài liệu.",
            newIndex.Count, allFiles.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    /// <summary>
    /// Cosine similarity giữa 2 vector đã chuẩn hóa.
    /// Kết quả trong khoảng [-1, 1], thường 0.4–0.95 cho văn bản liên quan.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0f, magA = 0f, magB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom < float.Epsilon ? 0f : dot / denom;
    }
}
