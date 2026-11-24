using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/warehouse-locations")]
[Authorize(Roles = "WAREHOUSE_MANAGER,ADMINISTRATOR")]
public sealed class WarehouseLocationsController(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor) : ControllerBase
{
    public sealed record WarehouseRequest(string? Code, string? Name, decimal MaxWeightKg);
    public sealed record WarehouseView(int Id, string Code, string Name, decimal? MaxWeightKg, decimal UsedWeightKg);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await using var db = new AppDbContext(options, accessor);
        var locations = await db.WarehouseLocations.AsNoTracking().Where(l => l.LocationStatus == "ACTIVE")
            .OrderBy(l => l.LocationCode)
            .Select(l => new WarehouseView(l.WarehouseLocationId, l.LocationCode, l.ZoneName, l.MaxWeightKg,
                l.Inventories.Sum(i => (decimal?)i.QuantityOnHand) ?? 0)).ToListAsync(ct);
        return Ok(ApiResponse<List<WarehouseView>>.Success(locations));
    }

    [HttpPost]
    public async Task<IActionResult> Create(WarehouseRequest request, CancellationToken ct)
    {
        var (code, name) = Validate(request);
        var result = await MutateAsync(async db =>
        {
            if (await db.WarehouseLocations.AnyAsync(l => l.LocationCode == code, ct))
                throw new ConflictException("Mã kho đã tồn tại, kể cả kho đã ngừng sử dụng.");
            var location = new WarehouseLocation { LocationCode = code, ZoneName = name, MaxWeightKg = request.MaxWeightKg, LocationStatus = "ACTIVE", CreatedAt = DateTime.UtcNow };
            db.WarehouseLocations.Add(location);
            await db.SaveChangesAsync(ct);
            return new WarehouseView(location.WarehouseLocationId, code, name, location.MaxWeightKg, 0);
        }, ct);
        return Ok(ApiResponse<WarehouseView>.Success(result, "Đã tạo kho."));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, WarehouseRequest request, CancellationToken ct)
    {
        var (code, name) = Validate(request);
        var result = await MutateAsync(async db =>
        {
            var location = await db.WarehouseLocations.SingleOrDefaultAsync(l => l.WarehouseLocationId == id && l.LocationStatus == "ACTIVE", ct)
                ?? throw new NotFoundException("Không tìm thấy kho đang hoạt động.");
            if (await db.WarehouseLocations.AnyAsync(l => l.WarehouseLocationId != id && l.LocationCode == code, ct))
                throw new ConflictException("Mã kho đã tồn tại.");
            if (await db.Inventories.AnyAsync(i => i.WarehouseLocationId == id && i.Unit != "kg", ct))
                throw new ConflictException("Tồn kho có đơn vị không tương thích với kg.");
            var used = await db.Inventories.Where(i => i.WarehouseLocationId == id).SumAsync(i => (decimal?)i.QuantityOnHand, ct) ?? 0;
            if (request.MaxWeightKg < used) throw new ConflictException("Sức chứa không thể nhỏ hơn lượng hàng đang lưu trong kho.");
            location.LocationCode = code; location.ZoneName = name; location.MaxWeightKg = request.MaxWeightKg;
            await db.SaveChangesAsync(ct);
            return new WarehouseView(id, code, name, request.MaxWeightKg, used);
        }, ct);
        return Ok(ApiResponse<WarehouseView>.Success(result, "Đã cập nhật kho."));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await MutateAsync(async db =>
        {
            var location = await db.WarehouseLocations.SingleOrDefaultAsync(l => l.WarehouseLocationId == id && l.LocationStatus == "ACTIVE", ct)
                ?? throw new NotFoundException("Không tìm thấy kho đang hoạt động.");
            if (await db.Inventories.AnyAsync(i => i.WarehouseLocationId == id && (i.QuantityOnHand != 0 || i.ReservedQuantity != 0), ct))
                throw new ConflictException("Không thể xóa kho còn hàng hoặc hàng đã giữ chỗ.");
            if (await db.GoodsReceipts.AnyAsync(r => r.WarehouseLocationId == id && r.ReceiptStatus == "DRAFT", ct))
                throw new ConflictException("Không thể xóa kho đang có phiếu nhập nháp. Hãy đổi kho trên phiếu trước.");
            location.LocationStatus = "INACTIVE";
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return Ok(ApiResponse.Success("Đã xóa kho khỏi danh sách sử dụng; lịch sử được giữ lại."));
    }

    private static (string Code, string Name) Validate(WarehouseRequest request)
    {
        var code = request.Code?.Trim() ?? ""; var name = request.Name?.Trim() ?? "";
        if (code.Length is < 1 or > 50 || name.Length is < 1 or > 100)
            throw new BadRequestException("Nhập mã kho (tối đa 50 ký tự) và tên kho (tối đa 100 ký tự).");
        if (request.MaxWeightKg <= 0 || request.MaxWeightKg > 999999999999999.999m || decimal.Round(request.MaxWeightKg, 3) != request.MaxWeightKg)
            throw new BadRequestException("Sức chứa phải là số dương, tối đa 3 chữ số thập phân.");
        return (code, name);
    }

    private async Task<T> MutateAsync<T>(Func<AppDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var context = new AppDbContext(options, accessor);
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = new AppDbContext(options, accessor);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX, HOLDLOCK)", ct);
            var result = await action(db);
            await tx.CommitAsync(ct);
            return result;
        });
    }
}
