using System.Text.Json.Nodes;
using SAW.Application.Features.AiChat;

namespace SAW.Infrastructure.AiChat;

public interface IAiChatModelClient
{
    Task<AiChatResponse> ChatAsync(
        string instructions,
        IReadOnlyList<(string Role, string Content)> history,
        string userMessage,
        int maxRequests,
        Func<string, JsonObject, CancellationToken, Task<string>> executeTool,
        CancellationToken ct);
}
