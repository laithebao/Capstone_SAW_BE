using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.QcInspections;
using SAW.Domain.Common;
using System.Security.Claims;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/qc-inspections")]
[Authorize]
public sealed class QcInspectionsController(IQcInspectionService service) : ControllerBase
{
    // ─── Helper: extract actor info from JWT ──────────────────────────────────

    private int ActorId => int.Parse(
        User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    private string ActorRole => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

    // =========================================================================
    // UC20 – Create Inspection Form
    // POST /api/qc-inspections
    // =========================================================================

    [HttpPost]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<QcInspectionDto>>> Create(
        [FromBody] CreateQcInspectionRequest request,
        CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ActorId, ct);
        return Created(
            $"api/qc-inspections/{result.Id}",
            ApiResponse<QcInspectionDto>.Created(result, "Tạo phiếu kiểm định thành công."));
    }

    // =========================================================================
    // UC21 – Declare Batch Sampling Ratio
    // PATCH /api/qc-inspections/{id}/sampling-ratio
    // =========================================================================

    [HttpPatch("{id:long}/sampling-ratio")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateSamplingRatio(
        long id,
        [FromBody] UpdateSamplingRatioRequest request,
        CancellationToken ct)
    {
        await service.UpdateSamplingRatioAsync(id, request, ct);
        return Ok(ApiResponse<object>.Success(new { }, "Cập nhật tỷ lệ lấy mẫu thành công."));
    }

    // =========================================================================
    // UC22 – Input Sensory Inspection Result
    // PUT /api/qc-inspections/{id}/sensory-result
    // =========================================================================

    [HttpPut("{id:long}/sensory-result")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<object>>> SaveSensoryResult(
        long id,
        [FromBody] SaveSensoryResultRequest request,
        CancellationToken ct)
    {
        await service.SaveSensoryResultAsync(id, request, ct);
        return Ok(ApiResponse<object>.Success(new { }, "Lưu kết quả cảm quan thành công."));
    }

    // =========================================================================
    // UC23 – Upload Quality Evidence Image
    // POST /api/qc-inspections/{id}/images
    // DELETE /api/qc-inspections/{id}/images/{imageId}
    // =========================================================================

    [HttpPost("{id:long}/images")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<QualityImageDto>>> AddImage(
        long id,
        [FromBody] UploadQualityImageRequest request,
        CancellationToken ct)
    {
        var result = await service.AddImageAsync(id, request, ActorId, ct);
        return Created(
            $"api/qc-inspections/{id}/images/{result.Id}",
            ApiResponse<QualityImageDto>.Created(result, "Tải ảnh bằng chứng thành công."));
    }

    [HttpDelete("{id:long}/images/{imageId:long}")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteImage(
        long id,
        long imageId,
        CancellationToken ct)
    {
        await service.DeleteImageAsync(id, imageId, ct);
        return Ok(ApiResponse<object>.Success(new { }, "Xóa ảnh bằng chứng thành công."));
    }

    // =========================================================================
    // UC24 – Input Laboratory Test Result
    // PUT /api/qc-inspections/{id}/lab-result
    // =========================================================================

    [HttpPut("{id:long}/lab-result")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<object>>> SaveLabResult(
        long id,
        [FromBody] SaveLabResultRequest request,
        CancellationToken ct)
    {
        await service.SaveLabResultAsync(id, request, ct);
        return Ok(ApiResponse<object>.Success(new { }, "Lưu kết quả kiểm nghiệm phòng lab thành công."));
    }

    // =========================================================================
    // UC22b – Input Environment Criteria Result
    // PUT /api/qc-inspections/{id}/environment-criteria
    // =========================================================================

    [HttpPut("{id:long}/environment-criteria")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<object>>> SaveEnvironmentCriteria(
        long id,
        [FromBody] SaveEnvironmentCriteriaRequest request,
        CancellationToken ct)
    {
        await service.SaveEnvironmentCriteriaAsync(id, request, ct);
        return Ok(ApiResponse<object>.Success(new { }, "Lưu kết quả tiêu chí môi trường thành công."));
    }

    // =========================================================================
    // UC52 + UC53 – Compare with Rule Set & Classify Grade (Finalize)
    // POST /api/qc-inspections/{id}/finalize
    // =========================================================================

    [HttpPost("{id:long}/finalize")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<FinalizeQcResultDto>>> Finalize(
        long id,
        CancellationToken ct)
    {
        var result = await service.FinalizeAsync(id, ct);
        return Ok(ApiResponse<FinalizeQcResultDto>.Success(
            result,
            result.QcResult == "PASS"
                ? $"Hoàn thành kiểm định. Lô hàng đạt hạng {result.QualityGrade}."
                : "Hoàn thành kiểm định. Lô hàng KHÔNG ĐẠT."));
    }

    // =========================================================================
    // UC54 – Reject Batch with Serious Defect
    // POST /api/qc-inspections/{id}/reject
    // =========================================================================

    [HttpPost("{id:long}/reject")]
    [Authorize(Roles = "ADMINISTRATOR")]   // UC54: only ADMINISTRATOR can manually override; system auto-rejects via Finalize
    public async Task<ActionResult<ApiResponse<object>>> RejectBatch(
        long id,
        [FromBody] RejectBatchRequest request,
        CancellationToken ct)
    {
        await service.RejectBatchAsync(id, request, ActorId, ct);
        return Ok(ApiResponse<object>.Success(new { }, "Từ chối lô hàng thành công."));
    }

    // =========================================================================
    // UC62 – View Inspection List
    // GET /api/qc-inspections
    // =========================================================================

    [HttpGet]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<PagedResult<QcInspectionListItem>>>> List(
        [FromQuery] string? batchCode,
        [FromQuery] string? inspectionCode,
        [FromQuery] string? status,
        [FromQuery] string? qcResult,
        [FromQuery] int? qcAccountId,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new QcInspectionListQuery(
            batchCode, inspectionCode, status, qcResult,
            qcAccountId, fromDate, toDate, page, pageSize);

        var result = await service.ListAsync(query, ActorId, ActorRole, ct);
        return Ok(ApiResponse<PagedResult<QcInspectionListItem>>.Success(result));
    }

    // =========================================================================
    // Detail view
    // GET /api/qc-inspections/{id}
    // =========================================================================

    [HttpGet("{id:long}")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<QcInspectionDetailDto>>> GetById(
        long id,
        CancellationToken ct)
    {
        var result = await service.GetByIdAsync(id, ct);
        return Ok(ApiResponse<QcInspectionDetailDto>.Success(result));
    }
}
