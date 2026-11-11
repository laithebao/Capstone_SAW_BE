using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiChat.Rag;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class KnowledgeBaseServiceTests
{
    private readonly Mock<IHttpClientFactory> factory = new(MockBehavior.Strict);
    private static OpenAIOptions OpenAI => new() { ApiKey = "test-only", EmbeddingDimensions = 2 };
    private static QdrantOptions Qdrant => new() { Endpoint = "https://knowledge.test", ApiKey = "test-only", CollectionAlias = "saw-test" };
    private KnowledgeBaseService Service(QdrantOptions? storeOptions = null, AiChatOptions? chatOptions = null) => new(
        new OpenAIEmbeddingService(factory.Object, OpenAI, NullLogger<OpenAIEmbeddingService>.Instance), [], chatOptions ?? new(), OpenAI,
        new QdrantKnowledgeStore(factory.Object, storeOptions ?? Qdrant), NullLogger<KnowledgeBaseService>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("http://knowledge.test")]
    [InlineData("https://knowledge.test/path")]
    [InlineData("https://user:password@knowledge.test")]
    [InlineData("https://knowledge.test/?query=1")]
    [InlineData("https://knowledge.test/#fragment")]
    [InlineData("not-an-endpoint")]
    public async Task UnsafeOrMissingStoreEndpoint_DisablesKnowledgeAccessWithoutAnyHttpRequest(string endpoint)
    {
        var service = Service(new QdrantOptions { Endpoint = endpoint, ApiKey = "test-only" });
        var status = await service.GetStatusAsync();
        Assert.False(status.IsReady); Assert.Equal(0, status.ChunkCount); Assert.Empty(status.IndexedFiles);
        Assert.Contains("tạm thời gián đoạn", await service.SearchAsync("Quy trình mua hàng?"));
        Assert.False(await service.BuildIndexAsync());
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("name with spaces")]
    [InlineData("name?query")]
    [InlineData("tài-liệu")]
    public async Task InvalidCollectionAlias_CannotBecomeARequestPath(string alias)
    {
        var service = Service(new QdrantOptions { Endpoint = "https://knowledge.test", ApiKey = "test-only", CollectionAlias = alias });
        Assert.False((await service.GetStatusAsync()).IsReady);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingApiKeyOrOversizedAlias_DoesNotConnectToStore()
    {
        Assert.False((await Service(new QdrantOptions { Endpoint = "https://knowledge.test", ApiKey = " " }).GetStatusAsync()).IsReady);
        Assert.False((await Service(new QdrantOptions { Endpoint = "https://knowledge.test", ApiKey = "test-only", CollectionAlias = new string('a', 65) }).GetStatusAsync()).IsReady);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankKnowledgeQuery_DoesNotSpendEmbeddingOrSearchRequests(string? query)
    {
        Assert.Contains("cung cấp nội dung", await Service().SearchAsync(query!));
        factory.VerifyNoOtherCalls();
    }

    private void Embeddings() => factory.Setup(f => f.CreateClient("OpenAIEmbedding")).Returns(() => new HttpClient(new Handler((_, _) =>
        Task.FromResult(Json("""{"data":[{"index":0,"embedding":[1,0]}]}""")))) { BaseAddress = new Uri("https://embeddings.test/v1/") });
    private void Store(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        factory.Setup(f => f.CreateClient("QdrantKnowledgeBase")).Returns(() => new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://knowledge.test/") });
    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Search_UsesEmbeddingProfileFilterAndReturnsOnlyRelevantStoredChunks()
    {
        Embeddings();
        Store(async (request, _) =>
        {
            Assert.Equal("/collections/saw-test/points/query", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(4, body.RootElement.GetProperty("limit").GetInt32());
            var filter = body.RootElement.GetProperty("filter").GetProperty("must")[0];
            Assert.Equal("embedding_profile", filter.GetProperty("key").GetString());
            Assert.Equal("text-embedding-3-small:2", filter.GetProperty("match").GetProperty("value").GetString());
            return Json("""{"status":"ok","result":{"points":[{"score":0.9,"payload":{"content":"Quy trình hiện tại"}},{"score":0.4,"payload":{"content":"Tài liệu không liên quan"}}]}}""");
        });
        var result = await Service(chatOptions: new() { KnowledgeBaseMinTopScore = 0.8f, KnowledgeBaseMinChunkScore = 0.5f }).SearchAsync("Đặt mua lô?");
        Assert.Equal("Quy trình hiện tại", result);
    }

    [Fact]
    public async Task Search_BelowTopScoreThreshold_DoesNotPresentUnrelatedDocumentAsAnAnswer()
    {
        Embeddings();
        Store((_, _) => Task.FromResult(Json("""{"status":"ok","result":{"points":[{"score":0.3,"payload":{"content":"Unrelated private text"}}]}}""")));
        var result = await Service(chatOptions: new() { KnowledgeBaseMinTopScore = 0.8f }).SearchAsync("Đặt mua lô?");
        Assert.Contains("không có tài liệu liên quan", result);
        Assert.DoesNotContain("Unrelated private text", result);
    }

    [Theory]
    [InlineData("{\"status\":\"ok\",\"result\":{\"points\":[]}}")]
    [InlineData("{\"status\":\"ok\",\"result\":{}}")]
    [InlineData("{\"status\":\"error\",\"result\":{\"points\":[]}}")]
    [InlineData("{\"status\":\"ok\",\"result\":{\"points\":[{\"score\":1}]}}")]
    [InlineData("invalid json")]
    public async Task Search_EmptyOrMalformedStoreResult_ReturnsUnavailableInsteadOfInventingContent(string json)
    {
        Embeddings(); Store((_, _) => Task.FromResult(Json(json)));
        Assert.Contains("tạm thời gián đoạn", await Service().SearchAsync("Quy trình mua lô?"));
    }

    [Fact]
    public async Task StoreHttpFailure_DegradesStatusAndSearchWithoutExposingProviderError()
    {
        Embeddings(); Store((_, _) => Task.FromResult(Json("secret provider error", HttpStatusCode.ServiceUnavailable)));
        var service = Service();
        Assert.False((await service.GetStatusAsync()).IsReady);
        var result = await service.SearchAsync("Quy trình?");
        Assert.Contains("tạm thời gián đoạn", result); Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task Status_ScansAllPagesAndReturnsDistinctSourceFilesInStableOrder()
    {
        var scrolls = 0;
        Store(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/aliases") return Json("""{"status":"ok","result":{"aliases":[{"alias_name":"saw-test","collection_name":"saw-version-1"}]}}""");
            Assert.Equal("/collections/saw-version-1/points/scroll", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (++scrolls == 1) return Json("""{"status":"ok","result":{"points":[{"payload":{"source_file":"b.txt"}},{"payload":{"source_file":"a.txt"}}],"next_page_offset":2}}""");
            Assert.Equal(2, body.RootElement.GetProperty("offset").GetInt32());
            return Json("""{"status":"ok","result":{"points":[{"payload":{"source_file":"a.txt"}}],"next_page_offset":null}}""");
        });
        var status = await Service().GetStatusAsync();
        Assert.True(status.IsReady); Assert.Equal(3, status.ChunkCount);
        Assert.Equal(new[] { "a.txt", "b.txt" }, status.IndexedFiles); Assert.Equal(2, scrolls);
    }

    [Fact]
    public async Task CancelledStatusRead_PropagatesCallerCancellation()
    {
        Store((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GetStatusAsync(cancellation.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
