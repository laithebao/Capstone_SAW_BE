using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.UserAccess;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.UserAccess;

public sealed class UserAccessRepositoryTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task AssignmentCatalogsExcludeInactiveRolesAndPermissions()
    {
        await using var db = Database();
        db.AddRange(new Role { RoleId = 1, RoleCode = "QC_STAFF", RoleName = "QC", IsActive = true },
            new Role { RoleId = 2, RoleCode = "OLD", RoleName = "Old", IsActive = false },
            new Permission { PermissionId = 1, PermissionCode = "READ", PermissionName = "Read", IsActive = true },
            new Permission { PermissionId = 2, PermissionCode = "OLD", PermissionName = "Old", IsActive = false });
        await db.SaveChangesAsync(); var repository = new UserAccessRepository(db);
        Assert.Equal(1, Assert.Single(await repository.GetRolesAsync(default)).RoleId);
        Assert.Null(await repository.GetActiveRoleAsync(2, default));
        Assert.Equal(1, Assert.Single(await repository.GetPermissionsAsync(default)).PermissionId);
        Assert.Equal(1, Assert.Single(await repository.GetPermissionsByIdsAsync([1, 2], default)).PermissionId);
        Assert.Empty(await repository.GetPermissionsByIdsAsync([], default));
    }

    [Fact]
    public async Task ReplacingOverridesRemovesRevokedEntriesAndPersistsOnlyTheNewAssignments()
    {
        await using var db = Database();
        var first = new Permission { PermissionId = 1, PermissionCode = "READ", PermissionName = "Read", IsActive = true };
        var second = new Permission { PermissionId = 2, PermissionCode = "WRITE", PermissionName = "Write", IsActive = true };
        var account = new Account { AccountId = 7, Username = "user7", Email = "user7@test.invalid", FullName = "User", PasswordHash = "hash",
            Role = new Role { RoleCode = "QC_STAFF", RoleName = "QC", IsActive = true } };
        account.AccountPermissions.Add(new AccountPermission { Permission = first, IsGranted = true });
        db.AddRange(account, second); await db.SaveChangesAsync();
        var repository = new UserAccessRepository(db);
        repository.ReplaceOverrides(account, [new(2, false)], [second]); await repository.SaveChangesAsync(default); db.ChangeTracker.Clear();
        var saved = await repository.GetByIdAsync(7, true, default);
        var assigned = Assert.Single(saved!.AccountPermissions);
        Assert.Equal(2, assigned.PermissionId); Assert.False(assigned.IsGranted); Assert.NotEqual(default, assigned.UpdatedAt);
        Assert.DoesNotContain(await db.AccountPermissions.ToListAsync(), item => item.PermissionId == 1);
    }
}
