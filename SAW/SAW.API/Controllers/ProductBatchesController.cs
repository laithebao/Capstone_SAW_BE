using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Interfaces;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/operation/product-batches")]
[Authorize(Roles = "OPERATION_STAFF")]
public sealed class ProductBatchesController(IProductBatchService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProductBatchListResponse>>> Search(
        [FromQuery] string? batchCode, [FromQuery] int? supplierId,
        [FromQuery] int? cropTypeId, [FromQuery] string? status,
        [FromQuery] string? sortBy, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(ApiResponse<ProductBatchListResponse>.Success(await service.SearchAsync(
            new ProductBatchQuery(batchCode, supplierId, cropTypeId, status, sortBy, page, pageSize),
            cancellationToken)));

    [HttpGet("filters")]
    public async Task<ActionResult<ApiResponse<ProductBatchFilterOptions>>> Filters(CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductBatchFilterOptions>.Success(await service.GetFilterOptionsAsync(cancellationToken)));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<ProductBatchDetail>>> Get(long id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductBatchDetail>.Success(await service.GetAsync(id, cancellationToken)));
}
