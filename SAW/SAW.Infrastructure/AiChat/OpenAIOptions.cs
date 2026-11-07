namespace SAW.Infrastructure.AiChat;

public sealed class OpenAIOptions
{
    private readonly string _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

    public string ApiKey
    {
        get => _apiKey;
        init
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim() != "YOUR_OPENAI_API_KEY_HERE")
                _apiKey = value.Trim();
        }
    }

    public string Model { get; init; } = "gpt-6-luna";
    public string ReasoningEffort { get; init; } = "low";
    public int MaxOutputTokens { get; init; } = 2048;
    public string EmbeddingModel { get; init; } = "text-embedding-3-small";
    public int EmbeddingDimensions { get; init; } = 1536;
    public int ForecastMaxOutputTokens { get; init; } = 4096;
    public int ForecastTimeoutSeconds { get; init; } = 90;
    public int ChatRequestTimeoutSeconds { get; init; } = 40;
    public int EmbeddingRequestTimeoutSeconds { get; init; } = 30;
}
