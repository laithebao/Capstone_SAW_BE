using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/operation/stock-adjustments")]
[Authorize(Roles = "OPERATION_STAFF")]
public sealed class StockAdjustmentsController(StockAdjustmentService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] StockAdjustmentQuery query, CancellationToken ct) =>
        Ok(ApiResponse<AdjustmentPage<AdjustmentSummary>>.Success(await service.SearchAsync(query, ct)));
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(ApiResponse<AdjustmentDetail>.Success(await service.GetAsync(id, ct)));
    [HttpGet("batches")]
    public async Task<IActionResult> Batches(string? search, CancellationToken ct, int page = 1, int pageSize = 20) =>
        Ok(ApiResponse<AdjustmentPage<AdjustmentBatch>>.Success(await service.BatchesAsync(search, page, pageSize, ct)));
    [HttpGet("locations")]
    public async Task<IActionResult> Locations(CancellationToken ct) => Ok(ApiResponse<IReadOnlyList<AdjustmentLocation>>.Success(await service.LocationsAsync(ct)));
    [HttpGet("batches/{id:long}/scope")]
    public async Task<IActionResult> Scope(long id, string kind, long? inventoryId, CancellationToken ct) =>
        Ok(ApiResponse<AdjustmentScope>.Success(await service.ScopeAsync(id, kind, inventoryId, ct)));
    [HttpPost]
    public async Task<IActionResult> Create(CreateStockAdjustmentRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AdjustmentDetail>.Success(await service.CreateAsync(request, Actor(), ct), "Đã gửi yêu cầu chờ Warehouse Manager duyệt; tồn kho chưa thay đổi."));
    [HttpPost("images")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > 10485760) throw new BadRequestException("Mỗi ảnh tối đa 10 MB.");
        using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct);
        return Ok(ApiResponse<AdjustmentImage>.Success(await service.UploadAsync(stream.ToArray(), Actor(), ct)));
    }
    [HttpDelete("images/{id:guid}")]
    public async Task<IActionResult> RemoveUpload(Guid id, CancellationToken ct)
    { await service.RemoveUploadAsync(id, Actor(), ct); return Ok(ApiResponse<bool>.Success(true)); }
    private int Actor() => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
}
