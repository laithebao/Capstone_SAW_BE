namespace SAW.Application.Features.Notifications;

public sealed record InboxItem(long Id, string Title, string Message, string? EntityType, string? EntityId, bool IsRead, DateTime CreatedAt);
public sealed record NotificationInbox(IReadOnlyList<InboxItem> Items, int UnreadCount, int TotalCount, int Page, int PageSize);
public interface INotificationInboxRepository
{
    Task<NotificationInbox> ListAsync(int actorId, int page, int pageSize, CancellationToken ct);
    Task ReadAsync(int actorId, long id, CancellationToken ct);
}
