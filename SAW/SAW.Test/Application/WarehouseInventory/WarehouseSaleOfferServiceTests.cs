using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.WarehouseInventory;

namespace SAW.Test.Application.WarehouseInventory;

public sealed class WarehouseSaleOfferServiceTests
{
    private readonly Mock<IWarehouseInventoryRepository> repository = new();

    [Fact]
    public async Task PublishSaleOffer_RejectsNonPositivePrice_AndDelegatesValidRequest()
    {
        var service = new WarehouseInventoryService(repository.Object);

        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateSaleOfferAsync(
            99, new WarehouseSaleOfferUpdateRequest(0, true), default));

        var expected = new WarehouseSaleOfferItem(
            99, "LOT-99", "Xoài", "Xoài cát", "A", "IN_STOCK",
            100, 100, 1_500_000, true, true, null, DateTime.UtcNow);
        repository.Setup(x => x.UpdateSaleOfferAsync(
                99, It.Is<WarehouseSaleOfferUpdateRequest>(r => r.WholeLotPrice == 1_500_000 && r.IsPublished), default))
            .ReturnsAsync(expected);

        var actual = await service.UpdateSaleOfferAsync(
            99, new WarehouseSaleOfferUpdateRequest(1_500_000, true), default);

        Assert.Same(expected, actual);
    }
}
