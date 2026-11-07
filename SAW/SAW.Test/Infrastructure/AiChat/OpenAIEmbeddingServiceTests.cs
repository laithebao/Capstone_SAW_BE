using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiChat.Rag;
using SAW.Infrastructure.AiChat.Rag.Loaders;
using Xunit;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class OpenAIEmbeddingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbedBatchAsync_ReturnsVectorsInInputOrder(bool useMaf)
    {
        var embedder = CreateEmbedder(request =>
        {
            request.RequestUri.Should().Be(new Uri("https://api.openai.com/v1/embeddings"));
            request.Headers.Authorization!.Parameter.Should().Be("test-key");
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            body["model"]!.GetValue<string>().Should().Be("text-embedding-3-small");
            body["input"]!.AsArray().Select(item => item!.GetValue<string>()).Should().Equal("Một", "Hai");
            body["dimensions"]!.GetValue<int>().Should().Be(2);
            return """{"object":"list","model":"text-embedding-3-small","data":[{"index":1,"embedding":[0,1]},{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":2,"total_tokens":2}}""";
        }, useMaf);

        var result = await embedder.EmbedBatchAsync(["Một", "Hai"], CancellationToken.None);

        result.Should().NotBeNull();
        result![0].Should().Equal(1, 0);
        result[1].Should().Equal(0, 1);
    }

    [Theory]
    [InlineData("""{"data":[{"index":0,"embedding":[1,0]},{"index":0,"embedding":[0,1]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[1]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1,0]}]}""")]
    public async Task EmbedBatchAsync_RejectsIncompleteOrInvalidBatch(string response)
    {
        var embedder = CreateEmbedder(_ => response);

        var result = await embedder.EmbedBatchAsync(["Một", "Hai"], CancellationToken.None);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KnowledgeBase_PublishesOnlyCompleteIndex(bool validSecondFile)
    {
        var corpus = Directory.CreateTempSubdirectory();
        try
        {
            const string orderGuide = "Hướng dẫn mua lô hàng.";
            const string storageGuide = "Hướng dẫn bảo quản.";
            await File.WriteAllTextAsync(Path.Combine(corpus.FullName, "01-orders.txt"), orderGuide);
            await File.WriteAllTextAsync(Path.Combine(corpus.FullName, "02-storage.txt"), storageGuide);

            var embedder = CreateEmbedder(request =>
            {
                var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                var input = body["input"]![0]!.GetValue<string>();
                if (input != storageGuide)
                    return """{"data":[{"index":0,"embedding":[1,0]}]}""";

                return validSecondFile
                    ? """{"data":[{"index":0,"embedding":[-1,0]}]}"""
                    : """{"data":[{"index":0,"embedding":[-1]}]}""";
            });
            var kb = new KnowledgeBaseService(embedder, [new PlainTextLoader()],
                new AiChatOptions { KnowledgeBasePath = corpus.FullName }, NullLogger<KnowledgeBaseService>.Instance);

            (await kb.BuildIndexAsync()).Should().Be(validSecondFile);
            var status = kb.GetStatus();
            status.IsReady.Should().Be(validSecondFile);
            status.ChunkCount.Should().Be(validSecondFile ? 2 : 0);
            var reply = await kb.SearchAsync("Mua lô thế nào?");
            reply.Should().Be(validSecondFile
                ? orderGuide
                : "Kho tri thức đang khởi tạo hoặc tạm thời gián đoạn. Vui lòng thử lại sau.");
        }
        finally
        {
            corpus.Delete(recursive: true);
        }
    }

    private static OpenAIEmbeddingService CreateEmbedder(Func<HttpRequestMessage, string> respond, bool useMaf = false)
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("OpenAIEmbedding"))
            .Returns(() => new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://api.openai.com/v1/") });

        return new OpenAIEmbeddingService(httpFactory.Object,
            new OpenAIOptions { ApiKey = "test-key", EmbeddingDimensions = 2 },
            NullLogger<OpenAIEmbeddingService>.Instance, new AiExecutionOptions { UseMafForChat = useMaf });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
