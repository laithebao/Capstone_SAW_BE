using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Domain.Common;
using SAW.Infrastructure.Inspection;
using SAW.Infrastructure.Persistence;
namespace SAW.API.Controllers;
[ApiController, Route("api/inspection-standards"), Authorize(Roles = "ADMINISTRATOR")]
public sealed class InspectionWorkbookController(AppDbContext db) : ControllerBase
{
    [HttpGet("template")]
    public async Task<IActionResult> Template(CancellationToken ct)
    {
        var code = await db.CropTypes.Where(x => x.IsActive).OrderBy(x => x.CropTypeId).Select(x => x.CropCode).FirstOrDefaultAsync(ct) ?? "CHON_MA_NONG_SAN_THUC_TE";
        return File(InspectionWorkbook.Template(code), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "SAW-Tieu-chuan-kiem-dinh.xlsx");
    }
    [HttpPost("import-preview"), RequestSizeLimit(InspectionImportLimits.MaxFileBytes + 65536)]
    public async Task<IActionResult> Preview(IFormFile file, CancellationToken ct)
    {
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file.Length > InspectionImportLimits.MaxFileBytes || file.Length == 0) return BadRequest(ApiResponse<object>.Failure(400, "Chỉ nhận xlsx, tối đa 5 MB."));
        using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct);
        var result = InspectionWorkbook.Parse(stream.ToArray());
        if (result.Request != null && result.Errors.Count == 0)
        {
            var cropId = await db.CropTypes.Where(x => x.CropCode == result.CropCode && x.IsActive).Select(x => (int?)x.CropTypeId).SingleOrDefaultAsync(ct);
            var errors = result.Errors.ToList();
            if (!cropId.HasValue) errors.Add(new(InspectionWorkbook.Info, 2, "CropCode", null, "Mã nông sản không tồn tại hoặc đã ngừng hoạt động."));
            if (string.IsNullOrWhiteSpace(result.Request.Code) || result.Request.Code.Length > 80 || string.IsNullOrWhiteSpace(result.Request.Name) || result.Request.Name.Length > 400 || result.Request.Description?.Length > 2000)
                errors.Add(new(InspectionWorkbook.Info, 2, "StandardCode/StandardName/Description", null, "Thông tin bắt buộc thiếu hoặc vượt độ dài cho phép."));
            if (await db.InspectionStandardSets.AnyAsync(x => x.StandardCode == result.Request.Code.Trim(), ct)) errors.Add(new(InspectionWorkbook.Info, 2, "StandardCode", null, "Mã bộ tiêu chuẩn đã tồn tại."));
            result = result with { Request = errors.Count == 0 ? result.Request with { CropTypeId = cropId!.Value } : null, Errors = errors };
        }
        return Ok(ApiResponse<WorkbookPreview>.Success(result));
    }
}
