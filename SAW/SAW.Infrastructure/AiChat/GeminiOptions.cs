namespace SAW.Infrastructure.AiChat;

public sealed class GeminiOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int MaxOutputTokens { get; init; } = 2048;
    public float Temperature { get; init; } = 0.2f;
    public int MaxMessagesPerSession { get; init; } = 10;
    public int RateLimitPerAccount { get; init; } = 30;
    public int RateLimitWindowMinutes { get; init; } = 10;

    /// <summary>
    /// Model embedding của Gemini dùng để tạo vector cho RAG Knowledge Base.
    /// Cấu hình trong appsettings: GeminiAI:EmbeddingModel
    /// Mặc định: "gemini-embedding-001" (768 chiều).
    /// </summary>
    public string EmbeddingModel { get; init; } = "gemini-embedding-001";

    /// <summary>
    /// Đường dẫn tới thư mục chứa các file tài liệu Knowledge Base (.md, .txt, .pdf, .docx).
    /// Có thể là đường dẫn tuyệt đối hoặc tương đối so với thư mục chạy ứng dụng.
    /// Mặc định: "KnowledgeBase" (cùng cấp với file thực thi).
    /// Cấu hình trong appsettings: GeminiAI:KnowledgeBasePath
    /// </summary>
    public string KnowledgeBasePath { get; init; } = "KnowledgeBase";
}

