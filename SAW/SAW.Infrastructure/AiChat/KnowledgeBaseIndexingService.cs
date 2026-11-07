using Microsoft.Extensions.Hosting;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Warms up the Knowledge Base without blocking application startup.
/// </summary>
public sealed class KnowledgeBaseIndexingService(
    KnowledgeBaseService knowledgeBase,
    OpenAIOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            return;

        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        for (var attempt = 1; attempt <= 3 && !knowledgeBase.IsReady; attempt++)
        {
            if (await knowledgeBase.BuildIndexAsync(stoppingToken))
                return;

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMinutes(attempt), stoppingToken);
        }
    }
}
