using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;

namespace SAW.Test.Application.DistributorOrders;

public sealed class DistributorOrderValidationTests
{
    private static readonly DateOnly Today = new(2026, 10, 7);
    private static CreateDistributorOrderRequest Valid() => new(Guid.NewGuid(), [new(1, 1500000m, 100m)],
        "  10 Đà Nẵng  ", "0901234567", Today.AddDays(1), "  Nguyên lô  ");

    [Fact]
    public void WholeLotRequest_HasNoEditableQuantity_AndTrimsDeliveryFields()
    {
        var normalized = DistributorOrderService.ValidateCreate(Valid(), Today);
        Assert.Equal("10 Đà Nẵng", normalized.DeliveryAddress);
        Assert.Equal("Nguyên lô", normalized.Note);
        Assert.DoesNotContain(typeof(SelectedLot).GetProperties(), p => p.Name is "Quantity" or "RequestedQuantity");
    }

    [Fact]
    public void RequestedReceiptDateIsOptional() =>
        Assert.Null(DistributorOrderService.ValidateCreate(Valid() with { ExpectedDeliveryDate = null }, Today).ExpectedDeliveryDate);

    [Fact]
    public void RepeatedLot_AndMissingLots_AreRejected()
    {
        var request = Valid();
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(request with { Lots = [new(1, 10, 100), new(1, 10, 100)] }, Today));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(request with { Lots = [] }, Today));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(request with { Lots = null! }, Today));
    }

    [Fact]
    public void InvalidPriceDateAndIdempotencyKey_AreRejected()
    {
        var request = Valid();
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(request with { RequestId = Guid.Empty }, Today));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(request with { ExpectedDeliveryDate = Today.AddDays(-1) }, Today));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(request with { Lots = [new(1, 0, 100)] }, Today));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(request with { ContactPhone = "invalid-phone" }, Today));
    }

    [Theory]
    [InlineData("090123456")]
    [InlineData("090123456a")]
    [InlineData("+84901234567")]
    [InlineData("090 123 4567")]
    [InlineData("٠٩٠١٢٣٤٥٦٧")]
    public void PhoneMustHaveAtLeastTenAsciiDigits(string phone) =>
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(Valid() with { ContactPhone = phone }, Today));

    [Theory]
    [InlineData("0901234567")]
    [InlineData("84901234567")]
    public void ValidNumericPhoneIsAccepted(string phone) =>
        Assert.Equal(phone, DistributorOrderService.ValidateCreate(Valid() with { ContactPhone = phone }, Today).ContactPhone);

    [Fact]
    public void InvalidDatesPagingAndStatus_AreRejected()
    {
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateQuery(new(Page: int.MaxValue)));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateQuery(new(PageSize: 100)));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateQuery(new(Status: "UNKNOWN")));
        Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateQuery(new(FromDate: Today, ToDate: Today.AddDays(-1))));
    }

}
