using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SAW.Application.Features.AiChat;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiChat.Rag;
using SAW.Infrastructure.Persistence;
using Xunit;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class AiChatServiceTests
{
    private readonly AiChatService _chatService;
    private readonly GeminiOptions _options;

    public AiChatServiceTests()
    {
        _options = new GeminiOptions
        {
            RateLimitPerAccount = 3,
            RateLimitWindowMinutes = 5,
            MaxMessagesPerSession = 10
        };

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().Options;
        var db = new AppDbContext(dbOptions);
        var httpFactoryMock = new Moq.Mock<IHttpClientFactory>();
        var embedder = new GeminiEmbeddingService(httpFactoryMock.Object, _options, NullLogger<GeminiEmbeddingService>.Instance);
        var kb = new KnowledgeBaseService(embedder, Array.Empty<IDocumentLoader>(), _options, NullLogger<KnowledgeBaseService>.Instance);
        var toolExecutor = new AiToolExecutor(db, NullLogger<AiToolExecutor>.Instance, kb);

        var geminiClient = new GeminiApiClient(new HttpClient(), _options, NullLogger<GeminiApiClient>.Instance);

        _chatService = new AiChatService(geminiClient, toolExecutor, _options);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendMessageAsync_WithEmptyOrWhitespaceMessage_ShouldReturnError(string? message)
    {
        // Arrange
        var request = new AiChatRequest(message!);

        // Act
        var response = await _chatService.SendMessageAsync(accountId: 101, request, CancellationToken.None);

        // Assert
        response.IsError.Should().BeTrue();
        response.Reply.Should().Contain("Tin nhắn không hợp lệ");
    }

    [Fact]
    public async Task SendMessageAsync_WithMessageExceeding2000Characters_ShouldReturnError()
    {
        // Arrange: Tin nhắn 2001 ký tự
        var longMessage = new string('A', 2001);
        var request = new AiChatRequest(longMessage);

        // Act
        var response = await _chatService.SendMessageAsync(accountId: 102, request, CancellationToken.None);

        // Assert
        response.IsError.Should().BeTrue();
        response.Reply.Should().Contain("tối đa 2000 ký tự");
    }

    [Fact]
    public async Task SendMessageAsync_WhenAccountExceedsRateLimit_ShouldReturnRateLimitError()
    {
        // Arrange: accountId riêng để không bị ảnh hưởng bởi test khác
        const int accountId = 9999;
        var request = new AiChatRequest("Xin chào AI");

        // Act: Gửi vượt ngưỡng RateLimitPerAccount (3 lần)
        // Vì tin nhắn hợp lệ sẽ tiến tới gọi HTTP GeminiApiClient (chưa có API key thực tế nên sẽ throw hoặc bị rate limit trước)
        // Ta test cơ chế rate limit:
        // Lần 1, 2, 3: count tăng lên
        // Lần 4: Chặn ngay tại bước CheckRateLimit mà không cần gọi đến Gemini!
        for (int i = 0; i < _options.RateLimitPerAccount; i++)
        {
            try
            {
                await _chatService.SendMessageAsync(accountId, request, CancellationToken.None);
            }
            catch
            {
                // Bỏ qua lỗi kết nối mạng HTTP của các lần gọi hợp lệ
            }
        }

        // Lần thứ 4: Phải bị Rate Limit chặn ngay lập tức
        var throttledResponse = await _chatService.SendMessageAsync(accountId, request, CancellationToken.None);

        // Assert
        throttledResponse.IsError.Should().BeTrue();
        throttledResponse.Reply.Should().Contain("quá");
        throttledResponse.Reply.Should().Contain("tin nhắn");
    }
}
