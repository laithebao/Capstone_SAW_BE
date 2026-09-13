namespace SAW.Domain.Entities;

public class Role
{
    public int RoleId { get; set; }
    public string RoleCode { get; set; } = default!;
    public string RoleName { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public ICollection<Account> Accounts { get; set; } = [];
}
