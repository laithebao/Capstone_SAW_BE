using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.Picking;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/operation/picking-orders")]
[Authorize(Roles = "OPERATION_STAFF")]
public sealed class PickingOrdersController(IPickingService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] PickingQuery query, CancellationToken ct) =>
        Ok(ApiResponse<PickingPage<PickingOrderSummary>>.Success(await service.SearchAsync(query, ct)));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        Ok(ApiResponse<PickingOrderDetail>.Success(await service.GetAsync(id, ct)));

    [HttpPost("{id:long}/confirm-picking")]
    public async Task<IActionResult> Confirm(long id, ConfirmPickingRequest request, CancellationToken ct) =>
        Ok(ApiResponse<PickingOrderDetail>.Success(await service.ConfirmAsync(id, request, ActorId(), ct), "Đã xác nhận soạn hàng."));

    private int ActorId() => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
}
