namespace SAW.Domain.Entities;

public class Permission
{
    public int PermissionId { get; set; }
    public string PermissionCode { get; set; } = default!;
    public string PermissionName { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsActive { get; set; }

    // Navigation
    public ICollection<AccountPermission> AccountPermissions { get; set; } = [];
}
