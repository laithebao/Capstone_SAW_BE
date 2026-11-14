using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.UserProfiles;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/user-profiles/me")]
[Authorize(Roles = "ADMINISTRATOR,WAREHOUSE_MANAGER,QC_STAFF,OPERATION_STAFF,DISTRIBUTOR")]
public sealed class UserProfilesController(IUserProfileService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<UserProfileResponse>>> Get(CancellationToken cancellationToken) =>
        Ok(ApiResponse<UserProfileResponse>.Success(await service.GetAsync(GetAccountId(), cancellationToken)));

    [HttpPut]
    public async Task<ActionResult<ApiResponse<UserProfileResponse>>> Update(UpdateUserProfileRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<UserProfileResponse>.Success(await service.UpdateAsync(GetAccountId(), request, cancellationToken), "Đã cập nhật hồ sơ."));

    private int GetAccountId()
    {
        var value = User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(value, out var accountId) || accountId <= 0) throw new UnauthorizedAccessException();
        return accountId;
    }
}
