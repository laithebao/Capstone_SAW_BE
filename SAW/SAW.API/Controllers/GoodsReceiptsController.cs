using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Features.GoodsReceipts.Interfaces;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/operation/goods-receipts")]
[Authorize(Roles = "OPERATION_STAFF")]
public sealed class GoodsReceiptsController(IGoodsReceiptService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] GoodsReceiptQuery query, CancellationToken ct) =>
        Ok(ApiResponse<GoodsReceiptPage<GoodsReceiptDetail>>.Success(await service.SearchAsync(query, ct)));

    [HttpGet("filters")]
    public async Task<IActionResult> Filters(CancellationToken ct) => Ok(ApiResponse<GoodsReceiptFilters>.Success(await service.FiltersAsync(ct)));

    [HttpGet("eligible-batches")]
    public async Task<IActionResult> Eligible([FromQuery] int? supplierId, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(ApiResponse<GoodsReceiptPage<GoodsReceiptBatch>>.Success(await service.EligibleAsync(supplierId, search, page, pageSize, ct)));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(ApiResponse<GoodsReceiptDetail>.Success(await service.GetAsync(id, ct)));

    [HttpPost]
    public async Task<IActionResult> Create(CreateGoodsReceiptRequest request, CancellationToken ct) =>
        Ok(ApiResponse<GoodsReceiptDetail>.Success(await service.CreateAsync(request, ActorId(), ct), "Đã lưu phiếu nhập nháp"));

    [HttpPut("{id:long}/confirm")]
    public async Task<IActionResult> Confirm(long id, ConfirmGoodsReceiptRequest request, CancellationToken ct) =>
        Ok(ApiResponse<GoodsReceiptDetail>.Success(await service.ConfirmAsync(id, request, ActorId(), ct), "Phiếu đã xác nhận nhập kho"));

    [HttpPut("{id:long}/draft")]
    public async Task<IActionResult> UpdateDraft(long id, UpdateGoodsReceiptDraftRequest request, CancellationToken ct) =>
        Ok(ApiResponse<GoodsReceiptDetail>.Success(await service.UpdateDraftAsync(id, request, ActorId(), ct), "Đã lưu thay đổi phiếu nháp"));

    private int ActorId() => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new UnauthorizedAccessException();
}
