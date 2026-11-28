using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // Cho phép tất cả user đã đăng nhập lấy danh sách Vùng trồng
public class GrowingAreasController : ControllerBase
{
    private readonly AppDbContext _context;

    public GrowingAreasController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Lấy danh sách tất cả các Vùng trồng cho Dropdown
    /// Endpoint: GET /api/GrowingAreas
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<SupplierGrowingAreaDto>>> GetGrowingAreas(CancellationToken cancellationToken)
    {
        var actor = User.FindFirst("account_id")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(actor, out var accountId) || accountId <= 0) return Unauthorized();
        var isStaff = User.IsInRole("ADMINISTRATOR") || User.IsInRole("OPERATION_STAFF") || User.IsInRole("WAREHOUSE_MANAGER");
        var growingAreas = await _context.Set<GrowingArea>()
            .Where(ga => ga.OwnerSupplierId == null || ga.OwnerSupplier!.AccountId == accountId || isStaff)
            .Select(ga => new SupplierGrowingAreaDto
            {
                GrowingAreaId = ga.GrowingAreaId,
                AreaName = ga.AreaName,
                Province = ga.Province,
                District = ga.District,
                Ward = ga.Ward
            })
            .ToListAsync(cancellationToken);

        return Ok(growingAreas);
    }
}
