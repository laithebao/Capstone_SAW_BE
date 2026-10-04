using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Interfaces;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/operation/product-batches")]
[Authorize(Roles = "OPERATION_STAFF,QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
public sealed class ProductBatchesController(IProductBatchService service) : ControllerBase
{
    [HttpGet("submitted-suppliers")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProductBatchFilterOption>>>> SubmittedSuppliers(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ProductBatchFilterOption>>.Success(
            await service.GetSubmittedSuppliersAsync(cancellationToken)));

    [HttpGet("submitted")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SubmittedDeclarationOption>>>> Submitted(
        [FromQuery] int supplierId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<SubmittedDeclarationOption>>.Success(
            await service.GetSubmittedBySupplierAsync(supplierId, cancellationToken)));

    [HttpGet("submitted/{id:long}")]
    public async Task<ActionResult<ApiResponse<SubmittedDeclarationDetail>>> SubmittedDetail(
        long id, [FromQuery] int supplierId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<SubmittedDeclarationDetail>.Success(
            await service.GetSubmittedDetailAsync(id, supplierId, cancellationToken)));

    [HttpPut("{id:long}/verify")]
    [Authorize(Roles = "OPERATION_STAFF")] // chỉ OPERATION_STAFF được xác nhận lô
    public async Task<ActionResult<ApiResponse<VerifyProductBatchResponse>>> Verify(
        long id, [FromQuery] int supplierId, [FromBody] VerifyProductBatchRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<VerifyProductBatchResponse>.Success(
            await service.VerifyAsync(id, supplierId, ActorId(), request, cancellationToken),
            "Product batch confirmed successfully."));

    [HttpPut("{id:long}/reject")]
    [Authorize(Roles = "OPERATION_STAFF")] // chỉ OPERATION_STAFF được từ chối lô
    public async Task<ActionResult<ApiResponse<RejectProductBatchResponse>>> Reject(
        long id, [FromQuery] int supplierId, [FromBody] RejectProductBatchRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RejectProductBatchResponse>.Success(
            await service.RejectAsync(id, supplierId, ActorId(), request, cancellationToken),
            "Product batch rejected."));

    [HttpPut("{id:long}/receiving-details")]
    [Authorize(Roles = "OPERATION_STAFF")] // chỉ OPERATION_STAFF được cập nhật
    public async Task<ActionResult<ApiResponse<ProductBatchDetail>>> UpdateReceivingDetails(
        long id, [FromBody] UpdateProductBatchReceivingRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ProductBatchDetail>.Success(
            await service.UpdateAsync(id, ActorId(), request, cancellationToken),
            "Receiving details updated."));

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
    public async Task<ActionResult<ApiResponse<ProductBatchDetail>>> Get(long id, CancellationToken cancellationToken)
    {
        var detail = await service.GetAsync(id, cancellationToken);
        if (!User.IsInRole("OPERATION_STAFF"))
            detail = detail with
            {
                CanUpdateReceivingInformation = false,
                ReceivingUpdateLockReason = detail.ReceivingUpdateLockReason
                    ?? "Bạn không có quyền cập nhật thông tin kiểm nhận."
            };
        return Ok(ApiResponse<ProductBatchDetail>.Success(detail));
    }

    private int ActorId()
    {
        var value = User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException();
    }
}
