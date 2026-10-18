using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.Notifications;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController, Authorize, Route("api/notifications")]
public sealed class NotificationsController(INotificationInboxRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        Ok(ApiResponse<NotificationInbox>.Success(await repository.ListAsync(Actor(), page, pageSize, ct)));
    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> Read(long id, CancellationToken ct) { await repository.ReadAsync(Actor(), id, ct); return NoContent(); }
    private int Actor() => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : throw new UnauthorizedAccessException();
}
