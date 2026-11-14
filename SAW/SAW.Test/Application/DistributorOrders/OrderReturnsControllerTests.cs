using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.API.Controllers;
using SAW.Domain.Common;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Application.DistributorOrders;

public sealed class OrderReturnsControllerTests
{
    [Fact]
    public void RetiredReturnCreationExplainsRefusalFlowWithoutOpeningDatabaseOrChangingStock()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
        var result = Assert.IsType<ObjectResult>(new OrderReturnsController(db).RequestReturn());
        Assert.Equal(410, result.StatusCode); var body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.False(body.IsSuccess); Assert.Equal(410, body.StatusCode); Assert.Contains("từ chối nhận hàng", body.Message);
        // No provider is configured: any attempted database access would fail this test.
    }
}
