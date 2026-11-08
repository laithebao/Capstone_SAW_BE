namespace SAW.Application.Features.SpoilageForecast;

public sealed record SpoilageForecastRequest(
    long ProductBatchId,
    bool ForceRefresh = false
);
