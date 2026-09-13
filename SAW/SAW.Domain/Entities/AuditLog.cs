namespace SAW.Domain.Entities;

public class AuditLog
{
    public long AuditLogId { get; set; }

    public int? AccountId { get; set; }

    public string ActionType { get; set; } = default!;
    public string EntityName { get; set; } = default!;
    public string? EntityId { get; set; }

    public string? OldDataJson { get; set; }
    public string? NewDataJson { get; set; }

    public string? Description { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation
    public Account? Account { get; set; }
}
