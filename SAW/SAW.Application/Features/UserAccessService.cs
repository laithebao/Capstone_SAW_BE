using SAW.Application.Exceptions;
using SAW.Domain.Entities;

namespace SAW.Application.Features.UserAccess;

public sealed record UserAccessListItem(
    int Id, string Username, string FullName, string Email, string RoleCode,
    string RoleName, string Status, DateTime? LastLoginAt, bool InactiveReviewFlag);

public sealed record UserAccessDetail(
    UserAccessListItem User, int RoleId, IReadOnlyList<PermissionAssignment> PermissionOverrides);

public sealed record PermissionAssignment(int PermissionId, string PermissionCode, string PermissionName, bool IsGranted);
public sealed record RoleOption(int Id, string Code, string Name);
public sealed record PermissionOption(int Id, string Code, string Name, string? Description);
public sealed record UserAccessListResponse(IReadOnlyList<UserAccessListItem> Items, int TotalCount, int Page, int PageSize);
public sealed record UpdateUserAccessRequest(int RoleId, IReadOnlyList<PermissionAssignmentInput> PermissionOverrides);
public sealed record PermissionAssignmentInput(int PermissionId, bool IsGranted);
public sealed record UpdateAccountStatusRequest(string Status);

public interface IUserAccessRepository
{
    Task<(IReadOnlyList<Account> Items, int TotalCount)> SearchAsync(string? search, int? roleId, string? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<Account?> GetByIdAsync(int accountId, bool includePermissions, CancellationToken cancellationToken);
    Task<Role?> GetActiveRoleAsync(int roleId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Permission>> GetPermissionsByIdsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken);
    void ReplaceOverrides(Account account, IReadOnlyList<PermissionAssignmentInput> overrides, IReadOnlyList<Permission> permissions);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IUserAccessService
{
    Task<UserAccessListResponse> SearchAsync(string? search, int? roleId, string? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<UserAccessDetail> GetAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleOption>> GetRolesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PermissionOption>> GetPermissionsAsync(CancellationToken cancellationToken);
    Task<UserAccessDetail> UpdateAsync(int targetId, int actorId, UpdateUserAccessRequest request, CancellationToken cancellationToken);
    Task<UserAccessListItem> UpdateStatusAsync(int targetId, int actorId, UpdateAccountStatusRequest request, CancellationToken cancellationToken);
}

public sealed class UserAccessService(IUserAccessRepository repository) : IUserAccessService
{
    private static readonly string[] AllowedStatuses = ["ACTIVE", "INACTIVE", "PENDING", "LOCKED"];

    public async Task<UserAccessListResponse> SearchAsync(string? search, int? roleId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await repository.SearchAsync(search?.Trim(), roleId, status?.Trim().ToUpperInvariant(), page, pageSize, cancellationToken);
        return new UserAccessListResponse(result.Items.Select(Map).ToList(), result.TotalCount, page, pageSize);
    }

    public async Task<UserAccessDetail> GetAsync(int id, CancellationToken cancellationToken) => Detail(await FindAsync(id, true, cancellationToken));
    public async Task<IReadOnlyList<RoleOption>> GetRolesAsync(CancellationToken cancellationToken) =>
        (await repository.GetRolesAsync(cancellationToken)).Select(x => new RoleOption(x.RoleId, x.RoleCode, x.RoleName)).ToList();
    public async Task<IReadOnlyList<PermissionOption>> GetPermissionsAsync(CancellationToken cancellationToken) =>
        (await repository.GetPermissionsAsync(cancellationToken)).Select(x => new PermissionOption(x.PermissionId, x.PermissionCode, x.PermissionName, x.Description)).ToList();

    public async Task<UserAccessDetail> UpdateAsync(int targetId, int actorId, UpdateUserAccessRequest request, CancellationToken cancellationToken)
    {
        var account = await FindAsync(targetId, true, cancellationToken);
        var role = await repository.GetActiveRoleAsync(request.RoleId, cancellationToken) ?? throw new BadRequestException("Vai trò không hợp lệ hoặc đã ngừng hoạt động.");
        if (targetId == actorId && role.RoleCode != "ADMINISTRATOR") throw new ForbiddenException("Bạn không thể tự gỡ quyền Quản trị viên của mình.");
        var duplicatePermission = request.PermissionOverrides.GroupBy(x => x.PermissionId).Any(x => x.Count() > 1);
        if (duplicatePermission) throw new BadRequestException("Danh sách quyền có dữ liệu trùng lặp.");
        var permissionIds = request.PermissionOverrides.Select(x => x.PermissionId).ToList();
        var permissions = await repository.GetPermissionsByIdsAsync(permissionIds, cancellationToken);
        if (permissions.Count != permissionIds.Count) throw new BadRequestException("Có quyền không hợp lệ hoặc đã ngừng hoạt động.");
        account.RoleId = role.RoleId; account.Role = role; account.UpdatedAt = DateTime.UtcNow;
        repository.ReplaceOverrides(account, request.PermissionOverrides, permissions);
        await repository.SaveChangesAsync(cancellationToken);
        return Detail(account);
    }

    public async Task<UserAccessListItem> UpdateStatusAsync(int targetId, int actorId, UpdateAccountStatusRequest request, CancellationToken cancellationToken)
    {
        var status = request.Status.Trim().ToUpperInvariant();
        if (!AllowedStatuses.Contains(status)) throw new BadRequestException("Trạng thái tài khoản không hợp lệ.");
        if (targetId == actorId && status != "ACTIVE") throw new ForbiddenException("Bạn không thể tự khóa hoặc vô hiệu hóa tài khoản của mình.");
        var account = await FindAsync(targetId, false, cancellationToken);
        account.AccountStatus = status; account.UpdatedAt = DateTime.UtcNow;
        await repository.SaveChangesAsync(cancellationToken);
        return Map(account);
    }

    private async Task<Account> FindAsync(int id, bool includePermissions, CancellationToken cancellationToken) =>
        await repository.GetByIdAsync(id, includePermissions, cancellationToken) ?? throw new NotFoundException("Không tìm thấy người dùng.");
    private static UserAccessDetail Detail(Account account) => new(Map(account), account.RoleId,
        account.AccountPermissions.Select(x => new PermissionAssignment(x.PermissionId, x.Permission.PermissionCode, x.Permission.PermissionName, x.IsGranted)).ToList());
    private static UserAccessListItem Map(Account account) => new(account.AccountId, account.Username, account.FullName, account.Email,
        account.Role.RoleCode, account.Role.RoleName, account.AccountStatus, account.LastLoginAt, account.InactiveReviewFlag);
}
