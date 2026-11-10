using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.AiChat;
using SAW.Domain.Common;
using SAW.Infrastructure.AiChat;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/distributor/ai-chat")]
[Authorize(Roles = "DISTRIBUTOR")]
public sealed class AiChatController(IAiChatService service, KnowledgeBaseService kbService) : ControllerBase
{
    /// <summary>
    /// POST api/distributor/ai-chat
    /// Send a chat message to the AI assistant.
    /// Requires: JWT Bearer token with role = DISTRIBUTOR.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] AiChatRequest request, CancellationToken ct)
    {
        var accountId = ActorId();
        var response = await service.SendMessageAsync(accountId, request, ct);
        // Always 200 — isError:true carries user-facing messages (rate limit, validation).
        // This prevents axios from throwing and showing a generic "network error" on the client.
        return Ok(ApiResponse<AiChatResponse>.Success(response));
    }

    /// <summary>
    /// GET api/distributor/ai-chat/kb-status
    /// Kiểm tra trạng thái Knowledge Base vector index.
    /// </summary>
    [HttpGet("kb-status")]
    public async Task<IActionResult> GetKbStatus(CancellationToken ct)
    {
        var (isReady, chunkCount, indexedFiles) = await kbService.GetStatusAsync(ct);
        return Ok(ApiResponse<object>.Success(new { isReady, chunkCount, indexedFiles }));
    }

    /// <summary>
    /// POST api/distributor/ai-chat/kb-reindex
    /// Yêu cầu tái lập chỉ mục (re-index) kho tri thức Knowledge Base.
    /// </summary>
    [HttpPost("kb-reindex")]
    public async Task<IActionResult> ReindexKb(CancellationToken ct)
    {
        if (!await kbService.BuildIndexAsync(ct))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse<object>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "Chưa thể tái lập chỉ mục Kho tri thức. Vui lòng thử lại sau."));

        var (_, chunkCount, indexedFiles) = await kbService.GetStatusAsync(ct);
        return Ok(ApiResponse<object>.Success(new
        {
            message = "Tái lập chỉ mục Knowledge Base thành công.",
            chunkCount,
            indexedFiles
        }));
    }

    private int ActorId() =>
        int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
}
