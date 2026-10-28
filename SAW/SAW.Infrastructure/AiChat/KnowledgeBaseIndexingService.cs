using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Background service chạy ngay sau khi server khởi động để xây dựng RAG vector index.
/// Việc indexing diễn ra ở background — không block startup, không block request.
/// Trong thời gian indexing, <see cref="KnowledgeBaseService.SearchAsync"/> sẽ trả về
/// thông báo "đang khởi tạo" thay vì kết quả.
/// </summary>
public sealed class KnowledgeBaseIndexingService(
    KnowledgeBaseService knowledgeBase,
    ILogger<KnowledgeBaseIndexingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Chờ 3 giây sau khi server ready để không tranh resource với các DI init khác
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        logger.LogInformation("[KnowledgeBase] Bắt đầu xây dựng RAG vector index...");

        try
        {
            await knowledgeBase.BuildIndexAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[KnowledgeBase] Indexing bị dừng do server shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[KnowledgeBase] Lỗi không mong đợi khi xây dựng index. " +
                "SearchKnowledgeBase sẽ không hoạt động cho đến khi server restart.");
        }
    }
}
