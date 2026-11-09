namespace SAW.Infrastructure.AiChat.Rag;

/// <summary>
/// Một đoạn văn bản (chunk) từ tài liệu Knowledge Base.
/// </summary>
internal sealed record KnowledgeChunk(
    /// <summary>Nội dung văn bản gốc của chunk.</summary>
    string Content,
    /// <summary>Tên file nguồn (dùng để debug/trace).</summary>
    string SourceFile,
    /// <summary>Thứ tự chunk trong file nguồn.</summary>
    int ChunkIndex);
