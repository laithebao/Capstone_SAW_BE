namespace SAW.Domain.Entities;

public class Notification
{
    public long NotificationId { get; set; }
    public int AccountId { get; set; }

    public string NotificationType { get; set; } = default!;
    public string Priority { get; set; } = "NORMAL";

    public string Title { get; set; } = default!;
    public string Message { get; set; } = default!;

    public string? RelatedEntityType { get; set; }
    public string? RelatedEntityId { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }

    // Navigation
    public Account Account { get; set; } = default!;
}
