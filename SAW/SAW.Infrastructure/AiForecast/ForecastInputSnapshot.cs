using SAW.Domain.Entities;

namespace SAW.Infrastructure.AiForecast;

public sealed record EnvironmentStats(
    decimal AvgTempC,
    decimal MinTempC,
    decimal MaxTempC,
    decimal? AvgHumidityPct,
    int TempDeviationCount,
    int LogCount,
    int PeriodHours
);

public sealed record ForecastInputSnapshot(
    ProductBatch Batch,
    QcInspection? QcInspection,
    EnvironmentStats? EnvStats,
    IReadOnlyList<EnvironmentLog> RecentEnvLogs,
    WarehouseLocation? CurrentLocation,
    int DaysFromHarvest,
    int? DaysUntilExpiry,
    decimal? ShelfLifeConsumedPct
);
