using System.Reflection;
using System.Text.Json;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.Picking;

public sealed class WholeLotFulfillmentTests
{
    // Keep production visibility unchanged while exercising the shared picking/issue policy.
    private static string? Reason(OrderDetail line, IReadOnlyCollection<InventoryReservation> holds,
        IReadOnlyCollection<Inventory> stocks, bool requirePicked = false) =>
        (string?)typeof(PickingRepository).Assembly.GetType("SAW.Infrastructure.Repositories.WholeLotFulfillment")!
            .GetMethod("Reason", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [line, holds, stocks, requirePicked]);

    private static (OrderDetail Line, Inventory[] Stocks, InventoryReservation[] Holds) Lot()
    {
        var stocks = new[]
        {
            new Inventory { InventoryId = 1, ProductBatchId = 7, QuantityOnHand = 60, ReservedQuantity = 60 },
            new Inventory { InventoryId = 2, ProductBatchId = 7, QuantityOnHand = 40, ReservedQuantity = 40 }
        };
        var holds = stocks.Select((stock, index) => new InventoryReservation
        {
            InventoryReservationId = index + 1, InventoryId = stock.InventoryId, Inventory = stock,
            ReservedQuantity = stock.QuantityOnHand, ReservationStatus = "RESERVED"
        }).ToArray();
        return (new OrderDetail { RequestedProductBatchId = 7, ApprovedWeightKg = 100 }, stocks, holds);
    }

    [Fact]
    public void WholeLotAcrossLocations_CanBePicked_ButCannotBeIssuedBeforeEveryReservationIsPicked()
    {
        var (line, stocks, holds) = Lot();
        Assert.Null(Reason(line, holds, stocks));
        Assert.NotNull(Reason(line, holds, stocks, requirePicked: true));
        foreach (var hold in holds) { hold.ReservationStatus = "PICKED"; hold.PickedQuantity = hold.ReservedQuantity; }
        Assert.Null(Reason(line, holds, stocks, requirePicked: true));
    }

    [Theory]
    [InlineData("missingBatch")]
    [InlineData("zeroApproved")]
    [InlineData("missingHold")]
    [InlineData("foreignBatch")]
    [InlineData("partialReservation")]
    [InlineData("releasedHold")]
    [InlineData("alreadyIssued")]
    public void MissingOrChangedWholeLotAllocation_BlocksPickingAndIssue(string change)
    {
        var (line, stocks, holds) = Lot();
        switch (change)
        {
            case "missingBatch": line.RequestedProductBatchId = null; break;
            case "zeroApproved": line.ApprovedWeightKg = 0; break;
            case "missingHold": holds = []; break;
            case "foreignBatch": stocks[0].ProductBatchId = 8; break;
            case "partialReservation": holds[0].ReservedQuantity = 59; break;
            case "releasedHold": holds[0].ReleasedAt = DateTime.UtcNow; break;
            case "alreadyIssued": holds[0].ReservationStatus = "ISSUED"; break;
        }
        Assert.NotNull(Reason(line, holds, stocks));
        Assert.NotNull(Reason(line, holds, stocks, requirePicked: true));
        Assert.Equal(change == "zeroApproved" ? 0 : 100, line.ApprovedWeightKg);
    }

    [Theory]
    [InlineData("shortStock")]
    [InlineData("extraStock")]
    [InlineData("otherOrderHold")]
    [InlineData("changedLocationWeights")]
    public void InventoryChangeCannotSilentlyResizeAnApprovedWholeLot(string change)
    {
        var (line, stocks, holds) = Lot();
        switch (change)
        {
            case "shortStock": stocks[0].QuantityOnHand = 59; break;
            case "extraStock": stocks[0].QuantityOnHand = 61; break;
            case "otherOrderHold": stocks[0].ReservedQuantity = 61; break;
            case "changedLocationWeights": stocks[0].QuantityOnHand = 50; stocks[1].QuantityOnHand = 50; break;
        }
        Assert.NotNull(Reason(line, holds, stocks));
        Assert.Equal(100, line.ApprovedWeightKg);
        Assert.Equal(100, holds.Sum(h => h.ReservedQuantity));
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(60.001)]
    public void PickedWeightCannotBeNegativeOrExceedItsReservation(decimal picked)
    {
        var (line, stocks, holds) = Lot(); holds[0].PickedQuantity = picked;
        Assert.NotNull(Reason(line, holds, stocks));
    }

    [Fact]
    public void HistoricalPartialPickingCanContinue_ButNeverPermitsPartialIssue()
    {
        var (line, stocks, holds) = Lot();
        holds[0].ReservationStatus = "PARTIALLY_PICKED"; holds[0].PickedQuantity = 20;
        Assert.Null(Reason(line, holds, stocks));
        Assert.NotNull(Reason(line, holds, stocks, requirePicked: true));
    }

    [Fact]
    public void ValidationDoesNotRewriteStockReservationsOrApprovedWeight()
    {
        var (line, stocks, holds) = Lot();
        var before = JsonSerializer.Serialize(new { line.ApprovedWeightKg, Stocks = stocks, Holds = holds });
        _ = Reason(line, holds, stocks, requirePicked: true);
        Assert.Equal(before, JsonSerializer.Serialize(new { line.ApprovedWeightKg, Stocks = stocks, Holds = holds }));
    }
}
