using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.UserAccess;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class UserAccessRepository(AppDbContext dbContext) : IUserAccessRepository
{
    public async Task<(IReadOnlyList<Account> Items, int TotalCount)> SearchAsync(
        string? search, int? roleId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Accounts.Include(x => x.Role).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.Username.Contains(search) || x.FullName.Contains(search) || x.Email.Contains(search));
        if (roleId.HasValue) query = query.Where(x => x.RoleId == roleId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.AccountStatus == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.FullName).ThenBy(x => x.AccountId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<Account?> GetByIdAsync(int accountId, bool includePermissions, CancellationToken cancellationToken)
    {
        IQueryable<Account> query = dbContext.Accounts.Include(x => x.Role);
        if (includePermissions) query = query.Include(x => x.AccountPermissions).ThenInclude(x => x.Permission);
        return query.SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
    }

    public Task<Role?> GetActiveRoleAsync(int roleId, CancellationToken cancellationToken) =>
        dbContext.Roles.SingleOrDefaultAsync(x => x.RoleId == roleId && x.IsActive, cancellationToken);
    public async Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken) =>
        await dbContext.Roles.Where(x => x.IsActive).OrderBy(x => x.RoleName).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken) =>
        await dbContext.Permissions.Where(x => x.IsActive).OrderBy(x => x.PermissionName).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<Permission>> GetPermissionsByIdsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await dbContext.Permissions.Where(x => ids.Contains(x.PermissionId) && x.IsActive).ToListAsync(cancellationToken);

    public void ReplaceOverrides(Account account, IReadOnlyList<PermissionAssignmentInput> overrides, IReadOnlyList<Permission> permissions)
    {
        dbContext.AccountPermissions.RemoveRange(account.AccountPermissions);
        account.AccountPermissions.Clear();
        var permissionById = permissions.ToDictionary(x => x.PermissionId);
        foreach (var item in overrides)
            account.AccountPermissions.Add(new AccountPermission { AccountId = account.AccountId, PermissionId = item.PermissionId, Permission = permissionById[item.PermissionId], IsGranted = item.IsGranted, UpdatedAt = DateTime.UtcNow });
    }
    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
