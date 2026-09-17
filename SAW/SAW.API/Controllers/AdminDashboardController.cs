using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.AdminDashboard;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/admin/dashboard")]
[Authorize(Roles = "ADMINISTRATOR")]
public sealed class AdminDashboardController(IAdminDashboardService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AdminDashboardResponse>>> Get(CancellationToken cancellationToken) =>
        Ok(ApiResponse<AdminDashboardResponse>.Success(await service.GetAsync(cancellationToken)));
}
