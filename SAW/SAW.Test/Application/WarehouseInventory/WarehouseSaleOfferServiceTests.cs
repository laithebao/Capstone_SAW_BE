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
        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateSaleOfferAsync(
            99, new WarehouseSaleOfferUpdateRequest(-1, false), default));
        repository.Verify(x => x.UpdateSaleOfferAsync(It.IsAny<long>(), It.IsAny<WarehouseSaleOfferUpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);

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

    [Fact]
    public async Task RepricingPreservesLoadedPublicationSnapshotAndDoesNotHideConcurrentStockConflict()
    {
        var request = new WarehouseSaleOfferUpdateRequest(100000, true, new string('A', 64));
        var conflict = new ConflictException("Recounted remainder or price changed");
        repository.Setup(x => x.UpdateSaleOfferAsync(99, request, default)).ThrowsAsync(conflict);
        Assert.Same(conflict, await Assert.ThrowsAsync<ConflictException>(() => new WarehouseInventoryService(repository.Object).UpdateSaleOfferAsync(99, request, default)));
        repository.Verify(x => x.UpdateSaleOfferAsync(99, request, default), Times.Once);
    }
}
