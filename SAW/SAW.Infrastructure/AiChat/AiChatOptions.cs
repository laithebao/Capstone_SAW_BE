namespace SAW.Infrastructure.AiChat;

public sealed class AiChatOptions
{
    public int MaxMessagesPerSession { get; init; } = 10;
    public int RateLimitPerAccount { get; init; } = 30;
    public int RateLimitWindowMinutes { get; init; } = 10;
    public int MaxModelRequestsPerTurn { get; init; } = 5;
    public int TurnTimeoutSeconds { get; init; } = 120;
    public string KnowledgeBasePath { get; init; } = "KnowledgeBase";
    public bool? KnowledgeBaseAutoSync { get; init; }
    public float KnowledgeBaseMinTopScore { get; init; } = 0.28f;
    public float KnowledgeBaseMinChunkScore { get; init; } = 0.25f;
}
