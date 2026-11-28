namespace SAW.Application.Features.AiChat;

public sealed record AiChatHistoryTurn(
    long Id, string Message, string Reply, DateTime SentAt, DateTime RepliedAt);

public sealed record AiChatHistoryPage(IReadOnlyList<AiChatHistoryTurn> Items, bool HasMore);

public interface IAiChatHistoryService
{
    Task<AiChatHistoryPage> GetHistoryAsync(int accountId, long? beforeId, CancellationToken ct);
    Task ClearHistoryAsync(int accountId, CancellationToken ct);
}
