using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.CropTypes;
using SAW.Domain.Common;
using SAW.Infrastructure.Inspection;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController, Route("api/crop-types"), Authorize(Roles = "ADMINISTRATOR")]
public sealed class CropTypeWorkbookController(AppDbContext db, ICropTypeService service) : ControllerBase
{
    [HttpGet("template")]
    public async Task<IActionResult> Template(CancellationToken ct) => File(
        CropTypeWorkbook.Template(await db.Categories.AsNoTracking().ToListAsync(ct)),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "SAW-Loai-nong-san.xlsx");

    private async Task<CropImportPreview> Read(IFormFile file, CancellationToken ct)
    {
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file.Length is 0 or > InspectionImportLimits.MaxFileBytes)
            throw new BadRequestException("Chỉ nhận file xlsx, tối đa 5 MB.");
        using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct);
        var preview = CropTypeWorkbook.Parse(stream.ToArray(), await db.Categories.AsNoTracking().ToListAsync(ct), DateTime.UtcNow);
        var errors = preview.Errors.ToList();
        foreach (var row in preview.Rows)
        {
            try { await service.ValidateBasicCreateAsync(row.Request, ct); }
            catch (Exception e) when (e is BadRequestException or ConflictException)
            { errors.Add(new(row.Row, "Name/Code/Category", e.Message)); }
        }
        return preview with { Errors = errors };
    }

    [HttpPost("import-preview"), RequestSizeLimit(InspectionImportLimits.MaxFileBytes + 65536)]
    public async Task<IActionResult> Preview(IFormFile file, CancellationToken ct) =>
        Ok(ApiResponse<CropImportPreview>.Success(await Read(file, ct)));

    [HttpPost("import"), RequestSizeLimit(InspectionImportLimits.MaxFileBytes + 65536)]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken ct) =>
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var preview = await Read(file, ct);
            if (preview.Errors.Count > 0) return BadRequest(ApiResponse<object>.BadRequest(
                "Chưa tạo loại nông sản nào. Vui lòng sửa lỗi và xem trước lại.", preview.Errors.Select(x => $"Dòng {x.Row} ({x.Column}): {x.Message}")));
            foreach (var row in preview.Rows) await service.CreateBasicAsync(row.Request, ct);
            await tx.CommitAsync(ct);
            return (IActionResult)Ok(ApiResponse<object>.Success(new { createdCount = preview.Rows.Count }, $"Đã tạo {preview.Rows.Count} loại nông sản."));
        });
}
