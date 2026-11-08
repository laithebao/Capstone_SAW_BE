using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.InspectionStandards;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/inspection-standards")]
[Authorize(Roles = "ADMINISTRATOR,QC_STAFF,WAREHOUSE_MANAGER")]
public sealed class InspectionStandardsController(IInspectionStandardService service) : ControllerBase
{
    // GET /api/inspection-standards?search=...
    [HttpGet]
    [Authorize(Roles = "ADMINISTRATOR")] // list sử dụng cơ sở
    public async Task<ActionResult<ApiResponse<IReadOnlyList<InspectionStandardListItem>>>> List(
        [FromQuery] string? search, CancellationToken token)
        => Ok(ApiResponse<IReadOnlyList<InspectionStandardListItem>>.Success(
            await service.ListAsync(search, token)));

    // GET /api/inspection-standards/published-versions?cropTypeId=...
    // Dùng khi tạo phiếu kiểm định — trả về VersionId thực sự (không phải SetId)
    [HttpGet("published-versions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PublishedVersionOption>>>> PublishedVersions(
        [FromQuery] int? cropTypeId,
        CancellationToken token)
        => Ok(ApiResponse<IReadOnlyList<PublishedVersionOption>>.Success(
            await service.GetPublishedVersionsAsync(cropTypeId, token)));

    // GET /api/inspection-standards/{id}
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<InspectionStandardDetailDto>>> GetById(
        int id, CancellationToken token)
        => Ok(ApiResponse<InspectionStandardDetailDto>.Success(
            await service.GetByIdAsync(id, token)));

    // POST /api/inspection-standards  (UC12)
    [HttpPost]
    [Authorize(Roles = "ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<InspectionStandardDto>>> Create(
        CreateInspectionStandardRequest request, CancellationToken token)
    {
        var result = await service.CreateAsync(request, token);
        return Created(
            $"api/inspection-standards/{result.Id}",
            ApiResponse<InspectionStandardDto>.Created(result, "Đã tạo bộ tiêu chuẩn kiểm định."));
    }

    // POST /api/inspection-standards/{id}/versions  (UC13)
    [HttpPost("{id:int}/versions")]
    [Authorize(Roles = "ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<InspectionStandardVersionCreatedDto>>> CreateVersion(
        int id, CreateInspectionStandardVersionRequest request, CancellationToken token)
    {
        var result = await service.CreateVersionAsync(id, request, token);
        return Created(
            $"api/inspection-standards/{id}",
            ApiResponse<InspectionStandardVersionCreatedDto>.Created(result, "Đã tạo phiên bản mới cho bộ tiêu chuẩn kiểm định."));
    }
}

