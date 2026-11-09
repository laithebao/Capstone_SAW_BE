using Microsoft.Extensions.Hosting;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Seeds or refreshes the Qdrant index during startup.
/// </summary>
public sealed class KnowledgeBaseIndexingService(
    KnowledgeBaseService knowledgeBase,
    AiChatOptions options,
    IHostEnvironment environment) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!knowledgeBase.IsConfigured)
            return;

        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        var autoSync = options.KnowledgeBaseAutoSync ?? environment.IsDevelopment();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var ready = autoSync
                ? await knowledgeBase.BuildIndexAsync(stoppingToken)
                : await knowledgeBase.EnsureIndexAsync(stoppingToken);
            if (ready)
                return;

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMinutes(attempt), stoppingToken);
        }
    }
}
