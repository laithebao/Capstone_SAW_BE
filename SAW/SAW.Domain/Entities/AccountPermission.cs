namespace SAW.Domain.Entities;

public class AccountPermission
{
    public int AccountId { get; set; }
    public int PermissionId { get; set; }
    public bool IsGranted { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Account Account { get; set; } = default!;
    public Permission Permission { get; set; } = default!;
}
