namespace SAW.Application.Features.SupplyContracts;

public static class SupplyContractQuotaPeriod
{
    public static (DateOnly Start, DateOnly End) GetBounds(
        DateOnly contractStart,
        DateOnly contractEnd,
        string deliveryPeriod,
        DateOnly deliveryDate)
    {
        if (deliveryDate < contractStart || deliveryDate > contractEnd)
            throw new ArgumentOutOfRangeException(nameof(deliveryDate));

        DateOnly periodStart;
        DateOnly periodEnd;
        if (deliveryPeriod == "WEEK")
        {
            var offset = deliveryDate.DayNumber - contractStart.DayNumber;
            periodStart = contractStart.AddDays(offset / 7 * 7);
            periodEnd = periodStart.AddDays(6);
        }
        else if (deliveryPeriod == "MONTH")
        {
            var offset = (deliveryDate.Year - contractStart.Year) * 12
                + deliveryDate.Month - contractStart.Month;
            periodStart = contractStart.AddMonths(offset);
            if (deliveryDate < periodStart)
                periodStart = contractStart.AddMonths(--offset);
            periodEnd = contractStart.AddMonths(offset + 1).AddDays(-1);
        }
        else
        {
            throw new ArgumentException("Chu kỳ giao hàng không hợp lệ.", nameof(deliveryPeriod));
        }

        return (periodStart, periodEnd > contractEnd ? contractEnd : periodEnd);
    }
}
