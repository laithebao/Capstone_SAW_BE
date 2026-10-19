using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Common;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.QrCodes.Dtos;
using SAW.Application.Features.QrCodes.Interfaces;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/distributor/orders")]
[Authorize(Roles = "DISTRIBUTOR")]
public sealed class DistributorOrdersController(IDistributorOrderService service, IStockAdjustmentResolutionRepository resolution,
    IProductBatchQrCodeService qrCodes) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct) =>
        Ok(ApiResponse<DistributorDashboard>.Success(await service.DashboardAsync(ActorId(), ct)));
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] DistributorQuery query, CancellationToken ct) =>
        Ok(ApiResponse<DistributorPage<CatalogLot>>.Success(await service.CatalogAsync(ActorId(), query, ct)));
    [HttpGet("catalog/{id:long}")]
    public async Task<IActionResult> Lot(long id, CancellationToken ct) =>
        Ok(ApiResponse<DistributorLotDetail>.Success(await service.LotAsync(ActorId(), id, ct)));
    [HttpGet("catalog/{id:long}/qr-code")]
    public async Task<IActionResult> LotQrCode(long id, CancellationToken ct)
    {
        // Apply the same catalog/owned-order visibility as the lot detail before exposing its QR.
        await service.LotAsync(ActorId(), id, ct);
        return Ok(ApiResponse<ProductBatchQrCodeResponse>.Success(await qrCodes.GetAsync(id, ct)));
    }
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DistributorQuery query, CancellationToken ct) =>
        Ok(ApiResponse<DistributorPage<DistributorOrderSummary>>.Success(await service.ListAsync(ActorId(), query, ct)));
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        Ok(ApiResponse<DistributorOrderDetail>.Success(await service.GetAsync(ActorId(), id, ct)));
    [HttpPost]
    public async Task<IActionResult> Create(CreateDistributorOrderRequest request, CancellationToken ct) =>
        Ok(ApiResponse<DistributorOrderDetail>.Success(await service.CreateAsync(ActorId(), request, ct), "Đã gửi đơn chờ duyệt."));
    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct) =>
        Ok(ApiResponse<DistributorOrderDetail>.Success(await service.CancelAsync(ActorId(), id, ct), "Đã hủy đơn."));
    [HttpPost("{id:long}/receive")]
    public async Task<IActionResult> Receive(long id, CancellationToken ct) =>
        Ok(ApiResponse<DistributorOrderDetail>.Success(await service.ConfirmReceiptAsync(ActorId(), id, ct), "Đã xác nhận nhận hàng."));
    [HttpGet("{id:long}/adjustments")]
    public async Task<IActionResult> Adjustments(long id, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<AdjustmentResolutionDetail>>.Success(await resolution.ForOrderAsync(id, ActorId(), ct)));
    [HttpPost("{id:long}/adjustments/respond")]
    public async Task<IActionResult> Respond(long id, AdjustmentResponseRequest request, CancellationToken ct) {
        await resolution.RespondAsync(id, request, ActorId(), ct);
        return Ok(ApiResponse<IReadOnlyList<AdjustmentResolutionDetail>>.Success(await resolution.ForOrderAsync(id, ActorId(), ct)));
    }
    private int ActorId() => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
}
