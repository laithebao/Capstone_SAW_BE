using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.SupplyContracts;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Contracts;
using SAW.Infrastructure.Persistence;
namespace SAW.API.Controllers;
[ApiController, Route("api/supply-contracts/preview"), Authorize(Roles = "WAREHOUSE_MANAGER")]
public sealed class ContractPreviewController(AppDbContext db, IConfiguration config) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Preview(CreateSupplyContractRequest request, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.SupplierId == request.SupplierId && x.ProfileStatus == "ACTIVE", ct);
        var crop = await db.CropTypes.AsNoTracking().SingleOrDefaultAsync(x => x.CropTypeId == request.CropTypeId && x.IsActive && x.SupplierCropTypes.Any(s => s.SupplierId == request.SupplierId && s.IsActive), ct);
        var draft = new SupplyContract { ContractCode = "DỰ THẢO", Revision = 1, DeliveryQuantityKg = request.DeliveryQuantityKg, DeliveryPeriod = request.DeliveryPeriod, UnitPriceVndPerKg = request.UnitPriceVndPerKg, StartDate = request.StartDate, EndDate = request.EndDate, Terms = request.Terms };
        return Ok(ApiResponse<object>.Success(new { content = ContractDocument.Build(draft, supplier, crop, config).Content }));
    }
}
