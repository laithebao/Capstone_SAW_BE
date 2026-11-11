using SAW.Application.Features.SupplyContracts;

namespace SAW.Test.Application.SupplyContracts;

public class SupplyContractQuotaPeriodTests
{
    [Theory]
    [InlineData("2026-01-01", "2026-01-10", "WEEK", "2026-01-01", "2026-01-01", "2026-01-07")]
    [InlineData("2026-01-01", "2026-01-10", "WEEK", "2026-01-07", "2026-01-01", "2026-01-07")]
    [InlineData("2026-01-01", "2026-01-10", "WEEK", "2026-01-10", "2026-01-08", "2026-01-10")]
    [InlineData("2024-01-31", "2024-04-30", "MONTH", "2024-02-28", "2024-01-31", "2024-02-28")]
    [InlineData("2024-01-31", "2024-04-30", "MONTH", "2024-02-29", "2024-02-29", "2024-03-30")]
    [InlineData("2025-12-15", "2026-02-28", "MONTH", "2026-01-01", "2025-12-15", "2026-01-14")]
    [InlineData("2026-01-01", "2026-01-01", "MONTH", "2026-01-01", "2026-01-01", "2026-01-01")]
    public void QuotaBoundariesStayAnchoredToContractStartAndHandleLeapMonthsAndShortFinalPeriods(string start, string end,
        string period, string delivery, string expectedStart, string expectedEnd)
    {
        var result = SupplyContractQuotaPeriod.GetBounds(DateOnly.Parse(start), DateOnly.Parse(end), period, DateOnly.Parse(delivery));
        Assert.Equal(DateOnly.Parse(expectedStart), result.Start); Assert.Equal(DateOnly.Parse(expectedEnd), result.End);
        Assert.InRange(DateOnly.Parse(delivery), result.Start, result.End);
    }

    [Theory]
    [InlineData("2025-12-31")]
    [InlineData("2026-02-01")]
    public void DeliveryOutsideContractCannotConsumeQuota(string delivery) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        SupplyContractQuotaPeriod.GetBounds(new(2026, 1, 1), new(2026, 1, 31), "WEEK", DateOnly.Parse(delivery)));

    [Fact]
    public void UnknownDeliveryPeriodIsRejected() => Assert.Throws<ArgumentException>(() =>
        SupplyContractQuotaPeriod.GetBounds(new(2026, 1, 1), new(2026, 1, 31), "DAY", new(2026, 1, 1)));

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
