using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<NotificationList>>> List(CancellationToken ct)
    {
        var accountId = ActorId();
        var items = await db.Notifications.AsNoTracking()
            .Where(notification => notification.AccountId == accountId)
            .OrderByDescending(notification => notification.CreatedAt)
            .Take(20)
            .Select(notification => new NotificationItem(
                notification.NotificationId,
                notification.NotificationType,
                notification.Title,
                notification.Message,
                notification.RelatedEntityType,
                notification.RelatedEntityId,
                notification.IsRead,
                notification.CreatedAt))
            .ToListAsync(ct);
        var unreadCount = await db.Notifications.CountAsync(
            notification => notification.AccountId == accountId && !notification.IsRead, ct);
        return Ok(ApiResponse<NotificationList>.Success(new NotificationList(items, unreadCount)));
    }

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id, CancellationToken ct)
    {
        var notification = await db.Notifications.SingleOrDefaultAsync(
            item => item.NotificationId == id && item.AccountId == ActorId(), ct);
        if (notification is null) return NotFound();

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Ok(ApiResponse<NotificationItem>.Success(new NotificationItem(
            notification.NotificationId,
            notification.NotificationType,
            notification.Title,
            notification.Message,
            notification.RelatedEntityType,
            notification.RelatedEntityId,
            notification.IsRead,
            notification.CreatedAt)));
    }

    private int ActorId()
    {
        var value = User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) && id > 0 ? id : throw new UnauthorizedAccessException();
    }
}

public sealed record NotificationItem(
    long Id,
    string Type,
    string Title,
    string Message,
    string? RelatedEntityType,
    string? RelatedEntityId,
    bool IsRead,
    DateTime CreatedAt);

public sealed record NotificationList(IReadOnlyList<NotificationItem> Items, int UnreadCount);
