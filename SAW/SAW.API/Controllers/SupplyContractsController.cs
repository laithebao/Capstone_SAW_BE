using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.SupplyContracts;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/supply-contracts")]
[Authorize(Roles = "WAREHOUSE_MANAGER,SUPPLIER,ADMINISTRATOR")]
public sealed class SupplyContractsController(ISupplyContractService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SupplyContractPage>>> List(
        [FromQuery] SupplyContractQuery query, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractPage>.Success(
            await service.ListAsync(ActorId(), ActorRole(), query, ct)));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> Get(long id, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.GetAsync(ActorId(), ActorRole(), id, ct)));

    [HttpGet("options")]
    [Authorize(Roles = "WAREHOUSE_MANAGER")]
    public async Task<ActionResult<ApiResponse<SupplyContractOptions>>> Options(CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractOptions>.Success(await service.GetOptionsAsync(ct)));

    [HttpGet("eligible-for-batch")]
    [Authorize(Roles = "SUPPLIER")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SupplyContractBatchOption>>>> EligibleForBatch(
        [FromQuery] DateOnly? expectedDeliveryDate, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<SupplyContractBatchOption>>.Success(
            await service.GetEligibleForBatchAsync(ActorId(), expectedDeliveryDate, ct)));

    [HttpPost]
    [Authorize(Roles = "WAREHOUSE_MANAGER")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> Create(
        CreateSupplyContractRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.CreateAsync(ActorId(), request, ct), "Đã gửi hợp đồng đến nhà cung cấp."));

    [HttpPut("{id:long}")]
    [Authorize(Roles = "WAREHOUSE_MANAGER")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> Update(
        long id, UpdateSupplyContractRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.UpdateByWarehouseAsync(ActorId(), id, request, ct),
            "Đã cập nhật và gửi lại hợp đồng đến nhà cung cấp xác nhận."));

    [HttpPost("{id:long}/supplier-cancel-request")]
    [Authorize(Roles = "SUPPLIER")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> SupplierCancelRequest(
        long id, SupplyContractReasonRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.RequestCancellationBySupplierAsync(ActorId(), id, request, ct),
            "Đã gửi yêu cầu hủy hợp đồng đến quản lý kho."));

    [HttpPost("{id:long}/warehouse-cancel")]
    [Authorize(Roles = "WAREHOUSE_MANAGER")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> WarehouseCancel(
        long id, SupplyContractReasonRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.CancelByWarehouseAsync(ActorId(), id, request, ct), "Đã hủy hợp đồng."));

    [HttpPost("{id:long}/supplier-confirm")]
    [Authorize(Roles = "SUPPLIER")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> SupplierConfirm(
        long id, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.ConfirmBySupplierAsync(ActorId(), id, ct), "Đã xác nhận và chuyển hợp đồng đến Admin."));

    [HttpPost("{id:long}/supplier-reject")]
    [Authorize(Roles = "SUPPLIER")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> SupplierReject(
        long id, RejectSupplyContractRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.RejectBySupplierAsync(ActorId(), id, request, ct), "Đã từ chối hợp đồng."));

    [HttpPost("{id:long}/admin-approve")]
    [Authorize(Roles = "ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> AdminApprove(
        long id, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.ApproveByAdminAsync(ActorId(), id, ct), "Đã duyệt hợp đồng."));

    [HttpPost("{id:long}/admin-reject")]
    [Authorize(Roles = "ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<SupplyContractDetail>>> AdminReject(
        long id, RejectSupplyContractRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SupplyContractDetail>.Success(
            await service.RejectByAdminAsync(ActorId(), id, request, ct), "Đã từ chối hợp đồng."));

    private int ActorId()
    {
        var value = User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) && id > 0 ? id : throw new UnauthorizedAccessException();
    }

    private string ActorRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
}
