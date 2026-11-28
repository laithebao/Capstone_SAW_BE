using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.AiChat;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.AiChat;

/// <summary>Persists completed turns and supplies account-owned history to the model.</summary>
public sealed class PersistentAiChatService(AiChatService chat, AppDbContext db)
    : IAiChatService, IAiChatHistoryService
{
    private const int PageSize = 50;

    public async Task<AiChatResponse> SendMessageAsync(int accountId, AiChatRequest request, CancellationToken ct)
    {
        var turns = await db.AiChatTurns.AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.AiChatTurnId).Take(PageSize).ToListAsync(ct);
        turns.Reverse();
        // Ignore client-supplied history; another distributor's messages must never enter this conversation.
        var history = turns.SelectMany(x => new[]
        {
            new ChatMessage("user", WithContext(x)),
            new ChatMessage("model", x.Reply)
        }).ToList();
        var sentAt = DateTime.UtcNow;
        var response = await chat.SendMessageAsync(accountId, request with { History = history }, ct);
        if (response.IsError) return response;

        var turn = new AiChatTurn
        {
            AccountId = accountId,
            Message = request.Message.Trim(),
            Reply = response.Reply,
            BatchContextId = request.BatchContextId,
            BatchContextCode = (string.IsNullOrWhiteSpace(request.BatchContextCode)
                ? request.BatchCode : request.BatchContextCode)?.Trim(),
            SentAt = sentAt,
            RepliedAt = DateTime.UtcNow
        };
        db.AiChatTurns.Add(turn);
        await db.SaveChangesAsync(ct);
        return response with { SavedTurn = ToDto(turn) };
    }

    public async Task<AiChatHistoryPage> GetHistoryAsync(int accountId, long? beforeId, CancellationToken ct)
    {
        var turns = await db.AiChatTurns.AsNoTracking()
            .Where(x => x.AccountId == accountId && (!beforeId.HasValue || x.AiChatTurnId < beforeId.Value))
            .OrderByDescending(x => x.AiChatTurnId).Take(PageSize + 1).ToListAsync(ct);
        var hasMore = turns.Count > PageSize;
        var items = turns.Take(PageSize).Reverse().Select(ToDto).ToList();
        return new(items, hasMore);
    }

    public async Task ClearHistoryAsync(int accountId, CancellationToken ct)
    {
        await db.AiChatTurns.Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
    }

    private static AiChatHistoryTurn ToDto(AiChatTurn turn) => new(
        turn.AiChatTurnId, turn.Message, turn.Reply,
        DateTime.SpecifyKind(turn.SentAt, DateTimeKind.Utc),
        DateTime.SpecifyKind(turn.RepliedAt, DateTimeKind.Utc));

    private static string WithContext(AiChatTurn turn) => !string.IsNullOrWhiteSpace(turn.BatchContextCode)
        ? $"[Nhà phân phối đang xem lô hàng có Mã lô (BatchCode): \"{turn.BatchContextCode}\"]\n{turn.Message}"
        : turn.BatchContextId.HasValue
            ? $"[Nhà phân phối đang xem lô hàng ID: {turn.BatchContextId}]\n{turn.Message}"
            : turn.Message;
}
