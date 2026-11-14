using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;
using SAW.Application.Features.WarehouseInventory;
using SAW.Domain.Common;

namespace SAW.Test.Application.WarehouseInventory;

public sealed class WarehouseOrdersControllerTests
{
    private readonly Mock<IWarehouseInventoryService> service = new(MockBehavior.Strict);
    private readonly Mock<IOrderReservationAllocator> allocator = new(MockBehavior.Strict);
    private WarehouseOrdersController Controller() => new(service.Object, allocator.Object)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", "7"), new Claim(ClaimTypes.Role, "WAREHOUSE_MANAGER")], "Test")) } }
    };

    [Fact]
    public async Task Reserve_WhenAllocationCannotComplete_ReturnsConflictWithoutReadingASuccessfulOrder()
    {
        allocator.Setup(x => x.TryReserveAsync(9, 7, default)).ReturnsAsync(false);
        await Assert.ThrowsAsync<ConflictException>(() => Controller().Reserve(9, default));
        service.VerifyNoOtherCalls();
        allocator.Verify(x => x.TryReserveAsync(9, 7, default), Times.Once);
    }

    [Fact]
    public async Task Reserve_AfterSuccessfulWholeLotAllocation_ReturnsPersistedOrderAndUsesAuthenticatedManager()
    {
        var order = new WarehouseDistributorOrderDetail(9, "PO-9", "RESERVED", "Buyer", 100, DateTime.UtcNow, null, "Address", null, null, true, []);
        allocator.Setup(x => x.TryReserveAsync(9, 7, default)).ReturnsAsync(true);
        service.Setup(x => x.GetDistributorOrderAsync(9, default)).ReturnsAsync(order);
        var result = Assert.IsType<OkObjectResult>(await Controller().Reserve(9, default));
        var body = Assert.IsType<ApiResponse<WarehouseDistributorOrderDetail>>(result.Value);
        Assert.Same(order, body.Data); Assert.Equal("RESERVED", body.Data!.Status);
        allocator.Verify(x => x.TryReserveAsync(9, 7, default), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Reserve_InvalidOrderIdDoesNotCallAllocator(long id)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Controller().Reserve(id, default));
        allocator.VerifyNoOtherCalls(); service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApproveAndRejectCarryTheAuthenticatedManagerToTheOrderWorkflow()
    {
        service.Setup(x => x.ApproveDistributorOrderAsync(7, 9, default)).ReturnsAsync((WarehouseDistributorOrderDetail)null!);
        service.Setup(x => x.RejectDistributorOrderAsync(7, 9, "Reason", default)).ReturnsAsync((WarehouseDistributorOrderDetail)null!);
        await Controller().Approve(9, default); await Controller().Reject(9, "Reason", default);
        service.Verify(x => x.ApproveDistributorOrderAsync(7, 9, default), Times.Once);
        service.Verify(x => x.RejectDistributorOrderAsync(7, 9, "Reason", default), Times.Once);
        allocator.VerifyNoOtherCalls();
    }
}
