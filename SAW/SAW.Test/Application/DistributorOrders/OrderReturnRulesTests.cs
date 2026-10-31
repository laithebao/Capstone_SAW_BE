using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;

namespace SAW.Test.Application.DistributorOrders;

public class OrderReturnRulesTests
{
    [Theory]
    [InlineData("PENDING")][InlineData("APPROVED")][InlineData("PARTIALLY_ISSUED")][InlineData("RETURNED")][InlineData("CANCELLED")]
    public void CannotRequestBeforeShipmentOrAfterClosure(string state) => Assert.Throws<ConflictException>(() => OrderReturnRules.EnsureRequestable(state));
    [Theory]
    [InlineData("ISSUED")][InlineData("DISPATCHED")][InlineData("DELIVERED")]
    public void ShippedOrdersCanBeReturned(string state) => OrderReturnRules.EnsureRequestable(state);
    [Fact]
    public void NotesMustExplainTheReturn()
    {
        Assert.Throws<BadRequestException>(() => OrderReturnRules.RequiredNote(" "));
        Assert.Throws<BadRequestException>(() => OrderReturnRules.RequiredNote(new string('a', 1001)));
        Assert.Equal("Hàng bị hỏng", OrderReturnRules.RequiredNote(" Hàng bị hỏng "));
    }
}
