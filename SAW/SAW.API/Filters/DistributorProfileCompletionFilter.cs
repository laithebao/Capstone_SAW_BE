using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SAW.API.Controllers;
using SAW.Application.Features.UserProfiles;

namespace SAW.API.Filters;

public sealed class DistributorProfileCompletionFilter(IUserProfileService profiles) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (!user.IsInRole("DISTRIBUTOR") || context.Controller is UserProfilesController or AuthController)
        { await next(); return; }
        var claim = user.FindFirstValue("account_id") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(claim, out var id) || id <= 0) { context.Result = new UnauthorizedResult(); return; }
        var profile = await profiles.GetAsync(id, context.HttpContext.RequestAborted);
        if (!profile.IsProfileComplete)
        {
            context.Result = new ObjectResult(new { code = "DISTRIBUTOR_PROFILE_INCOMPLETE",
                message = "Vui lòng hoàn thiện hồ sơ trước khi sử dụng các chức năng của hệ thống." }) { StatusCode = 403 };
            return;
        }
        await next();
    }
}
