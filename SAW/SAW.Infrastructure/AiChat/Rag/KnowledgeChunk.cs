namespace SAW.Infrastructure.AiChat.Rag;

/// <summary>
/// Một đoạn văn bản (chunk) đã được embedding từ tài liệu Knowledge Base.
/// </summary>
internal sealed record KnowledgeChunk(
    /// <summary>Vector embedding của chunk (768 chiều, Gemini text-embedding-004).</summary>
    float[] Embedding,
    /// <summary>Nội dung văn bản gốc của chunk.</summary>
    string Content,
    /// <summary>Tên file nguồn (dùng để debug/trace).</summary>
    string SourceFile,
    /// <summary>Thứ tự chunk trong file nguồn.</summary>
    int ChunkIndex);
