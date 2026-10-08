using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.WarehouseInventory;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/warehouse-manager/orders")]
[Authorize(Roles = "WAREHOUSE_MANAGER")]
public sealed class WarehouseOrdersController(IWarehouseInventoryService service) : ControllerBase
{
    private int ActorId() => int.Parse(User.FindFirst("account_id")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "0");

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => Ok(ApiResponse<WarehouseDistributorOrderPage>.Success(await service.GetDistributorOrdersAsync(status, page, pageSize, ct)));
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(ApiResponse<WarehouseDistributorOrderDetail>.Success(await service.GetDistributorOrderAsync(id, ct)));
    [HttpPost("{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, CancellationToken ct) => Ok(ApiResponse<WarehouseDistributorOrderDetail>.Success(await service.ApproveDistributorOrderAsync(ActorId(), id, ct)));
}
