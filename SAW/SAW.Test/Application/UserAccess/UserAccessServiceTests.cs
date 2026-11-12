using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.UserAccess;
using SAW.Domain.Entities;

namespace SAW.Test.Application.UserAccess;

public sealed class UserAccessServiceTests
{
    private readonly Mock<IUserAccessRepository> repository = new();
    private readonly Role admin = new() { RoleId = 1, RoleCode = "ADMINISTRATOR", RoleName = "Admin" };
    private readonly Role staff = new() { RoleId = 3, RoleCode = "QC_STAFF", RoleName = "QC" };
    private readonly Account account = new() { AccountId = 7, Username = "staff", FullName = "Test", Email = "test@saw.local", AccountStatus = "ACTIVE" };
    private UserAccessService Service => new(repository.Object);
    public UserAccessServiceTests()
    {
        account.Role = admin;
        account.RoleId = 1;
        repository.Setup(x => x.GetByIdAsync(7, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(account);
        repository.Setup(x => x.GetActiveRoleAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(admin);
        repository.Setup(x => x.GetActiveRoleAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(staff);
        repository.Setup(x => x.GetPermissionsByIdsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Permission>());
    }

    [Fact]
    public async Task Search_NormalizesFiltersAndBoundsPagination()
    {
        repository.Setup(x => x.SearchAsync("test", 1, "ACTIVE", 1, 100, It.IsAny<CancellationToken>())).ReturnsAsync((new[] { account }, 5));
        var result = await Service.SearchAsync(" test ", 1, " active ", 0, 101, default);
        Assert.Equal(5, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.Equal("ADMINISTRATOR", Assert.Single(result.Items).RoleCode);
    }

    [Fact]
    public async Task ReadDetailAndOptions_MapRolesAndPermissions()
    {
        var permission = new Permission { PermissionId = 2, PermissionCode = "READ", PermissionName = "Read", Description = "Read data" };
        account.AccountPermissions.Add(new AccountPermission { PermissionId = 2, Permission = permission, IsGranted = false });
        repository.Setup(x => x.GetRolesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { admin, staff });
        repository.Setup(x => x.GetPermissionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { permission });
        Assert.False(Assert.Single((await Service.GetAsync(7, default)).PermissionOverrides).IsGranted);
        Assert.Equal(2, (await Service.GetRolesAsync(default)).Count);
        Assert.Equal("READ", Assert.Single(await Service.GetPermissionsAsync(default)).Code);
    }

    [Fact]
    public async Task SelfDemotion_IsForbidden_AndDoesNotSave()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Service.UpdateAsync(7, 7, new(3, []), default));
        Assert.Equal(1, account.RoleId);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("LOCKED")][InlineData("INACTIVE")][InlineData("PENDING")]
    public async Task SelfLockOrDeactivation_IsForbidden(string status)
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Service.UpdateStatusAsync(7, 7, new(status), default));
        Assert.Equal("ACTIVE", account.AccountStatus);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(" active ", "ACTIVE")][InlineData("locked", "LOCKED")][InlineData("inactive", "INACTIVE")][InlineData("pending", "PENDING")]
    public async Task OtherAccountStatus_IsNormalizedAndSaved(string input, string expected)
    {
        var result = await Service.UpdateStatusAsync(7, 1, new(input), default);
        Assert.Equal(expected, result.Status);
        Assert.NotNull(account.UpdatedAt);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownStatusOrRole_IsRejectedWithoutSaving()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.UpdateStatusAsync(7, 1, new("INVALID"), default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.UpdateAsync(7, 1, new(99, []), default));
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DuplicateOrUnknownPermission_IsRejectedWithoutMutation()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.UpdateAsync(7, 1, new(3, [new(2, true), new(2, false)]), default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.UpdateAsync(7, 1, new(3, [new(99, true)]), default));
        Assert.Equal(1, account.RoleId);
        repository.Verify(x => x.ReplaceOverrides(It.IsAny<Account>(), It.IsAny<IReadOnlyList<PermissionAssignmentInput>>(), It.IsAny<IReadOnlyList<Permission>>()), Times.Never);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ValidRoleAndPermissionUpdate_ReplacesOverridesAndSaves()
    {
        IReadOnlyList<Permission> permissions = new[] { new Permission { PermissionId = 2, PermissionCode = "READ", PermissionName = "Read" } };
        var request = new UpdateUserAccessRequest(3, [new(2, false)]);
        repository.Setup(x => x.GetPermissionsByIdsAsync(It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 2 })), It.IsAny<CancellationToken>())).ReturnsAsync(permissions);
        var result = await Service.UpdateAsync(7, 1, request, default);
        Assert.Equal(3, result.RoleId);
        Assert.Equal("QC_STAFF", result.User.RoleCode);
        repository.Verify(x => x.ReplaceOverrides(account, request.PermissionOverrides, permissions), Times.Once);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdministratorCanKeepOwnRoleAndActiveStatus()
    {
        await Service.UpdateAsync(7, 7, new(1, []), default);
        await Service.UpdateStatusAsync(7, 7, new("ACTIVE"), default);
        Assert.Equal(1, account.RoleId);
        Assert.Equal("ACTIVE", account.AccountStatus);
    }

    [Fact]
    public async Task MissingAccount_ReturnsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service.GetAsync(99, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Service.UpdateAsync(99, 1, new(3, []), default));
        await Assert.ThrowsAsync<NotFoundException>(() => Service.UpdateStatusAsync(99, 1, new("ACTIVE"), default));
    }
}
