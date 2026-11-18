using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/warehouse-settings/capacity")]
[Authorize(Roles = "WAREHOUSE_MANAGER,ADMINISTRATOR")]
public sealed class WarehouseCapacitySettingsController(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor) : ControllerBase
{
    public sealed record CapacityView(decimal? MaxCapacityKg, decimal CurrentStoredWeightKg);
    public sealed record UpdateCapacityRequest(decimal MaxCapacityKg);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        await using var db = new AppDbContext(options, accessor);
        var settings = await db.WarehouseSettings.AsNoTracking().Take(2).ToListAsync(ct);
        if (settings.Count > 1) throw new ConflictException("Có nhiều cấu hình kho. Vui lòng kiểm tra dữ liệu trước khi chỉnh sức chứa.");
        if (await db.Inventories.AnyAsync(i => i.Unit != "kg", ct))
            throw new ConflictException("Tồn kho có đơn vị không tương thích với kg. Vui lòng kiểm tra dữ liệu kho.");
        var used = await db.Inventories.SumAsync(i => (decimal?)i.QuantityOnHand, ct) ?? 0;
        return Ok(ApiResponse<CapacityView>.Success(new(settings.SingleOrDefault()?.MaxCapacityKg, used)));
    }

    [HttpPut]
    public async Task<IActionResult> Update(UpdateCapacityRequest request, CancellationToken ct)
    {
        if (request.MaxCapacityKg <= 0 || request.MaxCapacityKg > 999999999999999.999m
            || decimal.Round(request.MaxCapacityKg, 3) != request.MaxCapacityKg)
            throw new BadRequestException("Sức chứa phải là số dương, tối đa 3 chữ số thập phân.");

        await using var strategyContext = new AppDbContext(options, accessor);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            await using var db = new AppDbContext(options, accessor);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            // Use the same inventory lock as receipt confirmation so an incoming lot cannot
            // invalidate the capacity check between validation and saving.
            await db.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX, HOLDLOCK)", ct);
            if (await db.Inventories.AnyAsync(i => i.Unit != "kg", ct))
                throw new ConflictException("Tồn kho có đơn vị không tương thích với kg. Vui lòng kiểm tra dữ liệu kho.");
            var used = await db.Inventories.SumAsync(i => (decimal?)i.QuantityOnHand, ct) ?? 0;
            if (request.MaxCapacityKg < used)
                throw new ConflictException("Sức chứa mới không thể nhỏ hơn khối lượng tồn kho hiện tại.");
            var settings = await db.WarehouseSettings.Take(2).ToListAsync(ct);
            if (settings.Count > 1) throw new ConflictException("Có nhiều cấu hình kho. Vui lòng kiểm tra dữ liệu trước khi chỉnh sức chứa.");
            var setting = settings.SingleOrDefault();
            if (setting is null)
            {
                setting = new WarehouseSetting { WarehouseSettingId = 1, WarehouseName = "Kho chính" };
                db.WarehouseSettings.Add(setting);
            }
            setting.MaxCapacityKg = request.MaxCapacityKg;
            setting.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); // AppDbContext records actor and change in audit log.
            await transaction.CommitAsync(ct);
            return new CapacityView(setting.MaxCapacityKg, used);
        });
        return Ok(ApiResponse<CapacityView>.Success(result, "Đã cập nhật sức chứa kho."));
    }
}
