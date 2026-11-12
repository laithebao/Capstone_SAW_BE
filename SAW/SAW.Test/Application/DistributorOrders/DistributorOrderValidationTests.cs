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

    [Fact]
    public void WholeLotCountAndContactFieldsAcceptUpperBoundariesAndRejectExcess()
    {
        var request = Valid() with
        {
            Lots = Enumerable.Range(1, 20).Select(id => new SelectedLot(id, 1, 1)).ToList(),
            DeliveryAddress = new string('a', 500), ContactPhone = new string('1', 30), Note = new string('n', 1000)
        };
        Assert.Equal(20, DistributorOrderService.ValidateCreate(request, Today).Lots.Count);
        foreach (var invalid in new[]
        {
            request with { Lots = [.. request.Lots, new(21, 1, 1)] }, request with { Lots = [new(1, 1, 0)] },
            request with { Lots = [null!] }, request with { DeliveryAddress = new string('a', 501) },
            request with { DeliveryAddress = "line\nbreak" }, request with { ContactPhone = new string('1', 31) },
            request with { Note = new string('n', 1001) }
        }) Assert.Throws<BadRequestException>(() => DistributorOrderService.ValidateCreate(invalid, Today));
    }

    [Theory]
    [InlineData("PARTIALLY_REFUSED")]
    [InlineData("DELIVERY_REFUSED")]
    [InlineData("RETURNED")]
    public void UpdatedOrderStatusesRemainSupportedByListFilters(string status) => DistributorOrderService.ValidateQuery(new(Status: status));

    [Fact]
    public async Task Create_UsesCurrentActorAndWholeLotSnapshots_AndKeepsRequestedReceiptDateOptional()
    {
        var repository = new Mock<IDistributorOrderRepository>(MockBehavior.Strict);
        var request = Valid() with { ExpectedDeliveryDate = null, ContactPhone = " 0901234567 ", Note = " " };
        var expected = new DistributorOrderDetail(9, "PO-9", "PENDING", "10 Đà Nẵng", "0901234567", null, null,
            1500000, DateTime.UtcNow, null, null, null, null, true, false, [new(1, "B-1", "Rice", 100, 1500000)], []);
        repository.Setup(r => r.CreateAsync(7, It.Is<CreateDistributorOrderRequest>(x => x.RequestId == request.RequestId
            && x.Lots == request.Lots && x.ExpectedDeliveryDate == null && x.DeliveryAddress == "10 Đà Nẵng"
            && x.ContactPhone == "0901234567" && x.Note == null), default)).ReturnsAsync(expected);
        var result = await new DistributorOrderService(repository.Object).CreateAsync(7, request, default);
        Assert.Same(expected, result); Assert.Equal(1500000, Assert.Single(result.Lines).WholeLotPrice);
        Assert.Null(result.ExpectedDeliveryDate); Assert.Equal("PENDING", result.Status);
        repository.VerifyAll();
    }

    [Fact]
    public async Task InvalidCreateIsRejectedBeforeCallingRepository_AndConflictsArePreserved()
    {
        var repository = new Mock<IDistributorOrderRepository>(MockBehavior.Strict); var service = new DistributorOrderService(repository.Object);
        await Assert.ThrowsAsync<BadRequestException>(() => service.CreateAsync(7, Valid() with { RequestId = Guid.Empty }, default));
        repository.VerifyNoOtherCalls();
        var request = Valid() with { ExpectedDeliveryDate = null };
        var conflict = new ConflictException("Lot already purchased");
        repository.Setup(r => r.CreateAsync(7, It.IsAny<CreateDistributorOrderRequest>(), default)).ThrowsAsync(conflict);
        Assert.Same(conflict, await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(7, request, default)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveOrderAndLotIdsCannotReadCancelOrConfirmReceipt(long id)
    {
        var repository = new Mock<IDistributorOrderRepository>(MockBehavior.Strict); var service = new DistributorOrderService(repository.Object);
        await Assert.ThrowsAsync<BadRequestException>(() => service.LotAsync(7, id, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.GetAsync(7, id, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.CancelAsync(7, id, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.ConfirmReceiptAsync(7, id, default));
        repository.VerifyNoOtherCalls();
    }

}
