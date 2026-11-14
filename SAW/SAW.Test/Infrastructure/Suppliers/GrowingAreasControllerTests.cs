using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.API.Controllers;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.Suppliers;

public sealed class GrowingAreasControllerTests
{
    [Fact]
    public async Task DropdownReturnsStoredAdministrativeFieldsWithoutModifyingGrowingAreas()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Add(new GrowingArea { GrowingAreaId = 1, AreaName = "Area", Region = "South", Province = "Province", District = "District", Ward = "Ward" }); await db.SaveChangesAsync();
        var result = await new GrowingAreasController(db).GetGrowingAreas(default);
        var item = Assert.Single(Assert.IsType<List<SupplierGrowingAreaDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(1, item.GrowingAreaId); Assert.Equal("Area", item.AreaName); Assert.Equal("Province", item.Province); Assert.Equal("District", item.District); Assert.Equal("Ward", item.Ward);
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(1, await db.Set<GrowingArea>().CountAsync());
    }
}
