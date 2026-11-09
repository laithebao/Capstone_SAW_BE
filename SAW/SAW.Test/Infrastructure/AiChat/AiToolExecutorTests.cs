using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Application.Features.DistributorOrders;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiChat.Rag;
using SAW.Infrastructure.Persistence;
using Xunit;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class AiToolExecutorTests
{
    private readonly AiToolExecutor _executor;

    public AiToolExecutorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().Options;
        var db = new AppDbContext(options);
        var logger = NullLogger<AiToolExecutor>.Instance;
        var kbLogger = NullLogger<KnowledgeBaseService>.Instance;

        // Khởi tạo dummy KnowledgeBaseService
        var kbOptions = new AiChatOptions();
        var httpFactoryMock = new Moq.Mock<IHttpClientFactory>();
        var openAI = new OpenAIOptions { ApiKey = "test-key" };
        var embedder = new OpenAIEmbeddingService(httpFactoryMock.Object, openAI, NullLogger<OpenAIEmbeddingService>.Instance);
        var store = new QdrantKnowledgeStore(httpFactoryMock.Object, new QdrantOptions());
        var kb = new KnowledgeBaseService(embedder, [], kbOptions, openAI, store, kbLogger);

        _executor = new AiToolExecutor(db, logger, kb, Mock.Of<IDistributorOrderService>());
    }

    [Fact]
    public async Task ExecuteAsync_WithUnsupportedTool_ShouldReturnUnsupportedErrorJson()
    {
        // Arrange
        const string unknownTool = "DeleteEntireWarehouseDatabase";
        var args = new JsonObject();

        // Act
        var result = await _executor.ExecuteAsync(unknownTool, args, accountId: 1, CancellationToken.None);

        // Assert
        ExtractError(result).Should().Contain("không được hỗ trợ").And.Contain(unknownTool);
    }

    [Fact]
    public async Task ExecuteAsync_GetBatchSummary_WithNullArgs_ShouldReturnMissingArgumentError()
    {
        // Act
        var result = await _executor.ExecuteAsync("GetBatchSummary", null, accountId: 1, CancellationToken.None);

        // Assert
        result.Should().Contain("error");
        ExtractError(result).Should().Contain("Thiếu tham số");
    }

    [Fact]
    public async Task ExecuteAsync_GetBatchSummary_WithEmptyBatchCode_ShouldReturnMissingBatchCodeError()
    {
        // Arrange
        var args = new JsonObject
        {
            ["otherProp"] = "123"
        };

        // Act
        var result = await _executor.ExecuteAsync("GetBatchSummary", args, accountId: 1, CancellationToken.None);

        // Assert
        ExtractError(result).Should().Contain("Thiếu tham số batchCode");
    }

    [Fact]
    public async Task ExecuteAsync_GetStorageAdvice_WithMissingCropTypeId_ShouldReturnMissingKeyError()
    {
        // Arrange
        var args = new JsonObject();

        // Act
        var result = await _executor.ExecuteAsync("GetStorageAdvice", args, accountId: 1, CancellationToken.None);

        // Assert
        ExtractError(result).Should().Contain("Thiếu tham số: cropTypeId");
    }

    [Fact]
    public async Task ExecuteAsync_SearchKnowledgeBase_WithMissingQuery_ShouldReturnMissingKeyError()
    {
        // Arrange
        var args = new JsonObject();

        // Act
        var result = await _executor.ExecuteAsync("SearchKnowledgeBase", args, accountId: 1, CancellationToken.None);

        // Assert
        ExtractError(result).Should().Contain("Thiếu tham số: query");
    }

    [Theory]
    [InlineData("GetBatchSummary")]
    [InlineData("GetBatchQcReport")]
    public async Task ExecuteAsync_BatchTools_WithNullArgs_CatchesArgumentExceptionSafely(string toolName)
    {
        // Act
        var result = await _executor.ExecuteAsync(toolName, null, accountId: 99, CancellationToken.None);

        // Assert
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().Contain("error");
    }

    [Theory]
    [InlineData("""{"search":"xoài","sort":"price_desc","page":0}""")]
    [InlineData("""{"search":"xoài","sort":"price_desc","page":2147483647}""")]
    [InlineData("""{"search":"xoài","sort":"unknown","page":1}""")]
    public async Task ExecuteAsync_SearchSaleLots_RejectsInvalidArguments(string args)
    {
        var result = await _executor.ExecuteAsync("SearchSaleLots", JsonNode.Parse(args), 1, CancellationToken.None);

        ExtractError(result).Should().Contain("tra cứu lô đang bán không hợp lệ");
    }

    private static string ExtractError(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() ?? "" : "";
    }
}
