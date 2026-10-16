using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.WarehouseInventory;
using SAW.Domain.Common;
using SAW.Application.Features.Picking;
using SAW.Application.Exceptions;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/warehouse-manager/orders")]
[Authorize(Roles = "WAREHOUSE_MANAGER")]
public sealed class WarehouseOrdersController(IWarehouseInventoryService service, IOrderReservationAllocator allocator) : ControllerBase
{
    private int ActorId() => int.Parse(User.FindFirst("account_id")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "0");

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => Ok(ApiResponse<WarehouseDistributorOrderPage>.Success(await service.GetDistributorOrdersAsync(status, page, pageSize, ct)));
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(ApiResponse<WarehouseDistributorOrderDetail>.Success(await service.GetDistributorOrderAsync(id, ct)));
    [HttpPost("{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, CancellationToken ct) => Ok(ApiResponse<WarehouseDistributorOrderDetail>.Success(await service.ApproveDistributorOrderAsync(ActorId(), id, ct)));
    [HttpPost("{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, [FromBody] string reason, CancellationToken ct) => Ok(ApiResponse<WarehouseDistributorOrderDetail>.Success(await service.RejectDistributorOrderAsync(ActorId(), id, reason, ct)));

    [HttpPost("{id:long}/reserve")]
    public async Task<IActionResult> Reserve(long id, CancellationToken ct)
    {
        if (id <= 0) throw new BadRequestException("Mã đơn không hợp lệ.");
        if (!await allocator.TryReserveAsync(id, ActorId(), ct))
            throw new ConflictException("Đơn chưa thể phân bổ đủ hàng. Vui lòng kiểm tra tồn kho, hạn sử dụng và vị trí kho.");
        return Ok(ApiResponse<WarehouseDistributorOrderDetail>.Success(await service.GetDistributorOrderAsync(id, ct), "Đã giữ đủ hàng cho đơn."));
    }
}
