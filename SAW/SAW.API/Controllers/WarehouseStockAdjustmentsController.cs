using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/warehouse-manager/stock-adjustments")]
[Authorize(Roles = "WAREHOUSE_MANAGER")]
public sealed class WarehouseStockAdjustmentsController(StockAdjustmentReviewService service, IStockAdjustmentResolutionRepository resolution) : ControllerBase
{
    [HttpGet("locations")]
    public async Task<IActionResult> Locations(CancellationToken ct) => Ok(ApiResponse<IReadOnlyList<AdjustmentLocation>>.Success(await service.LocationsAsync(ct)));
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] StockAdjustmentQuery query, CancellationToken ct) =>
        Ok(ApiResponse<AdjustmentPage<AdjustmentSummary>>.Success(await service.SearchAsync(query, ct)));
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(ApiResponse<AdjustmentReviewDetail>.Success(await service.GetAsync(id, ct)));
    [HttpPost("{id:long}/review")]
    public async Task<IActionResult> Review(long id, ReviewStockAdjustmentRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AdjustmentReviewDetail>.Success(await service.ReviewAsync(id, request, Actor(), ct),
            request.Decision == "APPROVE" ? "Đã duyệt yêu cầu và cập nhật tồn kho." : "Đã từ chối yêu cầu; tồn kho không thay đổi."));
    [HttpPost("{id:long}/proposal")]
    public async Task<IActionResult> Proposal(long id, AdjustmentProposalRequest request, CancellationToken ct) {
        await resolution.ActAsync(id, request, Actor(), ct);
        return Ok(ApiResponse<AdjustmentReviewDetail>.Success(await service.GetAsync(id, ct)));
    }
    private int Actor() => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
}
