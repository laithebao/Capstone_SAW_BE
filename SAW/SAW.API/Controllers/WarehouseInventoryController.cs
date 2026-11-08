using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.WarehouseInventory;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/warehouse-manager/inventory")]
[Authorize(Roles = "WAREHOUSE_MANAGER")]
public sealed class WarehouseInventoryController(IWarehouseInventoryService service) : ControllerBase
{
    [HttpGet("levels")]
    public async Task<IActionResult> Levels(CancellationToken ct) =>
        Ok(ApiResponse<WarehouseInventoryLevelChart>.Success(await service.GetLevelChartAsync(ct)));

    [HttpGet("capacity")]
    public async Task<IActionResult> Capacity(CancellationToken ct) =>
        Ok(ApiResponse<WarehouseCapacityChart>.Success(await service.GetCapacityChartAsync(ct)));

    [HttpGet("quality-distribution")]
    public async Task<IActionResult> QualityDistribution(CancellationToken ct) =>
        Ok(ApiResponse<ProductQualityDistributionChart>.Success(await service.GetQualityDistributionChartAsync(ct)));

    [HttpGet("sale-offers")]
    public async Task<IActionResult> SaleOffers(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(ApiResponse<WarehouseSaleOfferPage>.Success(
            await service.GetSaleOffersAsync(search, page, pageSize, ct)));

    [HttpPut("sale-offers/{batchId:long}")]
    public async Task<IActionResult> UpdateSaleOffer(
        long batchId, [FromBody] WarehouseSaleOfferUpdateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<WarehouseSaleOfferItem>.Success(
            await service.UpdateSaleOfferAsync(batchId, request, ct)));
}
