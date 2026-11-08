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
        var growingAreas = await _context.Set<GrowingArea>()
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