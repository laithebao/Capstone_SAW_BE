namespace SAW.Domain.Entities;

public class EmailVerificationToken
{
    public long EmailVerificationTokenId { get; set; }
    public int AccountId { get; set; }
    public string TokenHash { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    // Navigation
    public Account Account { get; set; } = default!;
}
