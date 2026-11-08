namespace SAW.Domain.Entities;

public class RefreshToken
{
    public long RefreshTokenId { get; set; }
    public int AccountId { get; set; }
    public string TokenHash { get; set; } = default!;
    public string? JwtId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public string? CreatedByIp { get; set; }
    public string? RevokedByIp { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    // Navigation
    public Account Account { get; set; } = default!;
}
