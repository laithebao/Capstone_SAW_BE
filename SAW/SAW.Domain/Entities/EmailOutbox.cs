namespace SAW.Domain.Entities;
public sealed class EmailOutbox
{
    public long Id { get; set; }
    public string EventKey { get; set; } = "";
    public int AccountId { get; set; }
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public string? LastError { get; set; }
}
