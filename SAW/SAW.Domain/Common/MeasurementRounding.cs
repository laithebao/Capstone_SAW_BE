namespace SAW.Domain.Common;

public static class MeasurementRounding
{
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    public static decimal? Round(decimal? value) => value.HasValue ? Round(value.Value) : null;
}
