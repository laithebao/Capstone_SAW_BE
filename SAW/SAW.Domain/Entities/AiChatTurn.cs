namespace SAW.Domain.Entities;

public sealed class AiChatTurn
{
    public long AiChatTurnId { get; set; }
    public int AccountId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Reply { get; set; } = string.Empty;
    public long? BatchContextId { get; set; }
    public string? BatchContextCode { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime RepliedAt { get; set; }
}
