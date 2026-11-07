namespace SAW.Infrastructure.AiForecast;

public interface IForecastModelClient
{
    Task<(string Json, string Model)> GenerateAsync(string instructions, string input, CancellationToken ct);
}
