using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.UserAccess;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/admin/user-access")]
[Authorize(Roles = "ADMINISTRATOR")]
public sealed class UserAccessController(IUserAccessService service) : ControllerBase
{
    [HttpGet("accounts")]
    public async Task<ActionResult<ApiResponse<UserAccessListResponse>>> Search(
        [FromQuery] string? search, [FromQuery] int? roleId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(ApiResponse<UserAccessListResponse>.Success(await service.SearchAsync(search, roleId, status, page, pageSize, cancellationToken)));

    [HttpGet("accounts/{id:int}")]
    public async Task<ActionResult<ApiResponse<UserAccessDetail>>> Get(int id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<UserAccessDetail>.Success(await service.GetAsync(id, cancellationToken)));

    [HttpGet("roles")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleOption>>>> Roles(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<RoleOption>>.Success(await service.GetRolesAsync(cancellationToken)));

    [HttpGet("permissions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PermissionOption>>>> Permissions(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<PermissionOption>>.Success(await service.GetPermissionsAsync(cancellationToken)));

    [HttpPut("accounts/{id:int}/access")]
    public async Task<ActionResult<ApiResponse<UserAccessDetail>>> Update(int id, UpdateUserAccessRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<UserAccessDetail>.Success(await service.UpdateAsync(id, ActorId(), request, cancellationToken), "Đã cập nhật quyền tài khoản."));

    [HttpPatch("accounts/{id:int}/status")]
    public async Task<ActionResult<ApiResponse<UserAccessListItem>>> UpdateStatus(int id, UpdateAccountStatusRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<UserAccessListItem>.Success(await service.UpdateStatusAsync(id, ActorId(), request, cancellationToken), "Đã cập nhật trạng thái tài khoản."));

    private int ActorId()
    {
        var value = User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException();
    }
}
