namespace SAW.Application.Features.AiChat;

/// <summary>
/// Interface của dịch vụ AI Chat.
/// </summary>
public interface IAiChatService
{
    Task<AiChatResponse> SendMessageAsync(int accountId, AiChatRequest request, CancellationToken ct);
}
