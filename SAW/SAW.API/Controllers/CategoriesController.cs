using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController, Route("api/categories"), Authorize]
public sealed class CategoriesController(AppDbContext db) : ControllerBase
{
    [HttpGet, Authorize(Roles = "ADMINISTRATOR")]
    public async Task<IActionResult> List(CancellationToken ct, string? search = null, bool? isActive = null)
    {
        var rows = await db.Categories.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(search)) rows = rows.Where(x => x.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || x.Code.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (isActive.HasValue) rows = rows.Where(x => x.IsActive == isActive).ToList();
        return Ok(ApiResponse<List<Category>>.Success(rows));
    }

    [HttpGet("lookup"), Authorize(Roles = "ADMINISTRATOR,SUPPLIER")]
    public async Task<IActionResult> Lookup(CancellationToken ct) => Ok(ApiResponse<object>.Success(
        await db.Categories.AsNoTracking().Where(x => x.IsActive && (x.ParentId == null || x.Parent!.IsActive))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new { x.CategoryId, x.ParentId, x.Name, x.Code, x.IsActive, x.SortOrder }).ToListAsync(ct)));

    [HttpGet("{id:int}/children"), Authorize(Roles = "ADMINISTRATOR")]
    public async Task<IActionResult> Children(int id, CancellationToken ct) => Ok(ApiResponse<object>.Success(
        await db.Categories.AsNoTracking().Where(x => x.ParentId == id).OrderBy(x => x.SortOrder)
            .Select(x => new { x.CategoryId, x.ParentId, x.Name, x.Code, x.IsActive, x.SortOrder }).ToListAsync(ct)));

    [HttpPost, Authorize(Roles = "ADMINISTRATOR")]
    public Task<IActionResult> Create(SaveCategory request, CancellationToken ct) => Save(null, request, ct);
    [HttpPut("{id:int}"), Authorize(Roles = "ADMINISTRATOR")]
    public Task<IActionResult> Update(int id, SaveCategory request, CancellationToken ct) => Save(id, request, ct);

    private Task<IActionResult> Save(int? id, SaveCategory request, CancellationToken ct) => Locked(async () =>
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[A-Z0-9_]+$")) throw new BadRequestException("Mã danh mục chỉ gồm chữ không dấu, số và dấu gạch dưới.");
        if (await db.Categories.AnyAsync(x => x.Code == code && x.CategoryId != id, ct)) throw new ConflictException("Mã danh mục đã tồn tại.");
        var entity = id.HasValue ? await db.Categories.SingleOrDefaultAsync(x => x.CategoryId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy danh mục.") : new Category();
        if (request.ParentId.HasValue)
        {
            var parent = await db.Categories.FindAsync([request.ParentId.Value], ct);
            if (parent is null || parent.ParentId.HasValue || parent.CategoryId == id) throw new BadRequestException("Chỉ hỗ trợ hai cấp danh mục, không được chọn chính nó làm cha.");
            if (id.HasValue && await db.Categories.AnyAsync(x => x.ParentId == id, ct)) throw new ConflictException("Danh mục đang có danh mục con.");
        }
        else if (id.HasValue && await db.CropTypes.AnyAsync(x => x.CategoryId == id, ct)) throw new ConflictException("Danh mục con đang được nông sản sử dụng, không thể chuyển thành danh mục cha.");
        var name = DisplayName.CapitalizeFirst(request.Name);
        if (name.Length == 0) throw new BadRequestException("Tên danh mục không được để trống.");
        entity.Name = name; entity.Code = code; entity.ParentId = request.ParentId;
        entity.IsActive = request.IsActive; entity.SortOrder = request.SortOrder; entity.UpdatedAt = DateTime.UtcNow;
        if (!id.HasValue) entity.SortOrder = (await db.Categories.Where(x => x.ParentId == request.ParentId)
            .MaxAsync(x => (int?)x.SortOrder, ct) ?? -1) + 1;
        if (!id.HasValue) db.Categories.Add(entity);
        await db.SaveChangesAsync(ct);
        var childIds = await db.Categories.Where(x => x.ParentId == entity.CategoryId).Select(x => x.CategoryId).ToListAsync(ct);
        if (entity.ParentId is null)
            await db.CropTypes.Where(x => x.CategoryId.HasValue && childIds.Contains(x.CategoryId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CategoryName, entity.Name), ct);
        else
        {
            var parentName = await db.Categories.Where(x => x.CategoryId == entity.ParentId).Select(x => x.Name).SingleAsync(ct);
            await db.CropTypes.Where(x => x.CategoryId == entity.CategoryId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CategoryName, parentName), ct);
        }
        return Ok(ApiResponse<object>.Success(new { entity.CategoryId, entity.ParentId, entity.Name, entity.Code, entity.IsActive, entity.SortOrder }));
    }, ct);

    [HttpDelete("{id:int}"), Authorize(Roles = "ADMINISTRATOR")]
    public IActionResult Delete(int id) => Conflict(new { message = "Danh mục không được xóa. Hãy chuyển sang trạng thái ngừng hoạt động." });

    private async Task<IActionResult> Locked(Func<Task<IActionResult>> action, CancellationToken ct) =>
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Categories.FromSqlRaw("SELECT * FROM CATEGORY WITH (UPDLOCK,HOLDLOCK)").LoadAsync(ct);
            var result = await action(); await tx.CommitAsync(ct); return result;
        });
}

public sealed record SaveCategory([Required, MaxLength(100)] string Name, [Required, MaxLength(30)] string Code,
    int? ParentId, bool IsActive = true, [Range(0, int.MaxValue)] int SortOrder = 0);
