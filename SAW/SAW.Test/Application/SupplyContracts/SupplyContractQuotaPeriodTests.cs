using SAW.Application.Features.SupplyContracts;

namespace SAW.Test.Application.SupplyContracts;

public class SupplyContractQuotaPeriodTests
{
    [Fact]
    public void WeekIsAnchoredToContractStart()
    {
        var bounds = SupplyContractQuotaPeriod.GetBounds(
            new DateOnly(2026, 10, 2), new DateOnly(2026, 12, 31), "WEEK", new DateOnly(2026, 10, 10));

        Assert.Equal(new DateOnly(2026, 10, 9), bounds.Start);
        Assert.Equal(new DateOnly(2026, 10, 15), bounds.End);
    }

    [Fact]
    public void MonthIsAnchoredToContractStartAndClampedToContractEnd()
    {
        var bounds = SupplyContractQuotaPeriod.GetBounds(
            new DateOnly(2026, 1, 31), new DateOnly(2026, 3, 15), "MONTH", new DateOnly(2026, 3, 10));

        Assert.Equal(new DateOnly(2026, 2, 28), bounds.Start);
        Assert.Equal(new DateOnly(2026, 3, 15), bounds.End);
    }
}
