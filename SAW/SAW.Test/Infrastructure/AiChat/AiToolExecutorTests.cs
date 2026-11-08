using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        var kbOptions = new GeminiOptions();
        var httpFactoryMock = new Moq.Mock<IHttpClientFactory>();
        var embedder = new GeminiEmbeddingService(httpFactoryMock.Object, kbOptions, NullLogger<GeminiEmbeddingService>.Instance);
        var kb = new KnowledgeBaseService(embedder, Array.Empty<IDocumentLoader>(), kbOptions, kbLogger);

        _executor = new AiToolExecutor(db, logger, kb);
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
        result.Should().Contain("không được hỗ trợ");
        result.Should().Contain(unknownTool);
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

    private static string ExtractError(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() ?? "" : "";
    }
}
