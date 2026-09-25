using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.CropTypes;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/crop-types")]
[Authorize] // CHỈ GIỮ [Authorize] CHUNG, BỎ Roles = "ADMINISTRATOR" Ở CẤP CLASS
public sealed class CropTypesController(ICropTypeService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "ADMINISTRATOR, SUPPLIER")] // Mở cho cả ADMINISTRATOR và SUPPLIER xem danh sách
    public async Task<ActionResult<ApiResponse<CropTypeListResponse>>> Search(
        [FromQuery] string? search, [FromQuery] string? category, [FromQuery] bool? isActive,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default) =>
        Ok(ApiResponse<CropTypeListResponse>.Success(await service.SearchAsync(search, category, isActive, page, pageSize, cancellationToken)));

    [HttpGet("{id:int}")]
    [Authorize(Roles = "ADMINISTRATOR, SUPPLIER")] // Mở cho cả 2 xem chi tiết
    public async Task<ActionResult<ApiResponse<CropTypeDto>>> Get(int id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<CropTypeDto>.Success(await service.GetAsync(id, cancellationToken)));

    [HttpPost]
    [Authorize(Roles = "ADMINISTRATOR")] // Chỉ ADMINISTRATOR mới có quyền Thêm
    public async Task<ActionResult<ApiResponse<CropTypeDto>>> Create(SaveCropTypeRequest request, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.Id }, ApiResponse<CropTypeDto>.Created(result, "Đã tạo loại nông sản."));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "ADMINISTRATOR")] // Chỉ ADMINISTRATOR mới có quyền Sửa
    public async Task<ActionResult<ApiResponse<CropTypeDto>>> Update(int id, SaveCropTypeRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<CropTypeDto>.Success(await service.UpdateAsync(id, request, cancellationToken), "Đã cập nhật loại nông sản."));

    [HttpPatch("{id:int}/status")]
    [Authorize(Roles = "ADMINISTRATOR")] // Chỉ ADMINISTRATOR mới có quyền Đổi trạng thái
    public async Task<ActionResult<ApiResponse<CropTypeDto>>> SetStatus(int id, SetCropTypeStatusRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<CropTypeDto>.Success(await service.SetStatusAsync(id, request.IsActive, cancellationToken), "Đã cập nhật trạng thái."));

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "ADMINISTRATOR")] // Chỉ ADMINISTRATOR mới có quyền Xóa
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return Ok(ApiResponse.Success("Đã xóa loại nông sản."));
    }
}