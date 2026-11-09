using System.Net;
using System.Text;
using System.Text.Json;
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
    [InlineData(false, """[{"index":0,"embedding":[1,0]},{"index":0,"embedding":[0,1]}]""")]
    [InlineData(true, """[{"index":0,"embedding":[1,0]},{"index":0,"embedding":[0,1]}]""")]
    [InlineData(false, """[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[1]}]""")]
    [InlineData(true, """[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[1]}]""")]
    [InlineData(false, """[{"index":0,"embedding":[1,0]}]""")]
    [InlineData(true, """[{"index":0,"embedding":[1,0]}]""")]
    [InlineData(false, """[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[0,0]}]""")]
    [InlineData(true, """[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[0,0]}]""")]
    public async Task EmbedBatchAsync_RejectsIncompleteOrInvalidBatch(bool useMaf, string data)
    {
        var embedder = CreateEmbedder(_ => $$$"""
            {"object":"list","model":"text-embedding-3-small","data":{{{data}}},"usage":{"prompt_tokens":2,"total_tokens":2}}
            """, useMaf);

        var result = await embedder.EmbedBatchAsync(["Một", "Hai"], CancellationToken.None);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task KnowledgeBase_PublishesOnlyCompleteIndex(bool validEmbeddings, bool uploadCompleted, bool hasPrevious)
    {
        var corpus = Directory.CreateTempSubdirectory();
        try
        {
            const string orderGuide = "Hướng dẫn mua lô hàng.";
            const string storageGuide = "Hướng dẫn bảo quản.";
            await File.WriteAllTextAsync(Path.Combine(corpus.FullName, "01-orders.txt"), orderGuide);
            await File.WriteAllTextAsync(Path.Combine(corpus.FullName, "02-storage.txt"), storageGuide);

            var documentEmbeddingRequests = 0;
            var openAI = new OpenAIOptions { ApiKey = "test-key", EmbeddingDimensions = 2 };
            var embedder = CreateEmbedder(request =>
            {
                var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                var inputs = body["input"]!.AsArray();
                if (inputs.Count == 1)
                    return """{"data":[{"index":0,"embedding":[1,0]}]}""";

                documentEmbeddingRequests++;
                inputs.Select(input => input!.GetValue<string>()).Should().Equal(orderGuide, storageGuide);
                return validEmbeddings
                    ? """{"data":[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[-1,0]}]}"""
                    : """{"data":[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[-1]}]}""";
            }, options: openAI);

            const string alias = "saw-kb-test";
            const string previousCollection = "saw-kb-test-previous";
            const string previousContent = "Quy trình trước đây.";
            const string completed = """{"status":"ok","result":{"status":"completed","operation_id":0}}""";
            var publish = validEmbeddings && uploadCompleted;
            string? activeCollection = hasPrevious ? previousCollection : null;
            string? draft = null;
            var draftDeleted = false;
            var qdrant = new QdrantOptions
            {
                Endpoint = "https://test.cloud.qdrant.io", ApiKey = "test-key", CollectionAlias = alias
            };

            var httpFactory = new Mock<IHttpClientFactory>();
            httpFactory.Setup(factory => factory.CreateClient("QdrantKnowledgeBase"))
                .Returns(() => new HttpClient(new StubHandler(request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (request.Method == HttpMethod.Get && path == "/aliases")
                    {
                        var aliases = activeCollection is null ? Array.Empty<object>()
                            : [new { alias_name = alias, collection_name = activeCollection }];
                        return JsonSerializer.Serialize(new { status = "ok", result = new { aliases } });
                    }

                    var body = request.Content is null ? null
                        : JsonNode.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());

                    if (request.Method == HttpMethod.Post && path == "/collections/aliases")
                    {
                        var actions = body!["actions"]!.AsArray();
                        actions.Count.Should().Be(hasPrevious ? 2 : 1);
                        if (hasPrevious)
                            actions[0]!["delete_alias"]!["alias_name"]!.GetValue<string>().Should().Be(alias);
                        actions[^1]!["create_alias"]!["collection_name"]!.GetValue<string>().Should().Be(draft);
                        activeCollection = draft;
                        return """{"status":"ok","result":true}""";
                    }

                    if (request.Method == HttpMethod.Put && !path.EndsWith("/index") && !path.EndsWith("/points"))
                    {
                        draft = path.Split('/').Last();
                        body!["vectors"]!["size"]!.GetValue<int>().Should().Be(2);
                        body["vectors"]!["distance"]!.GetValue<string>().Should().Be("Cosine");
                        return """{"status":"ok","result":true}""";
                    }

                    if (path == $"/collections/{draft}/index")
                    {
                        body!["field_name"]!.GetValue<string>().Should().Be("embedding_profile");
                        request.RequestUri.Query.Should().Be("?wait=true");
                        return completed;
                    }

                    if (path == $"/collections/{draft}/points")
                    {
                        request.RequestUri.Query.Should().Be("?wait=true");
                        var points = body!["points"]!.AsArray();
                        points.Select(point => point!["payload"]!["source_file"]!.GetValue<string>())
                            .Should().Equal("01-orders.txt", "02-storage.txt");
                        return uploadCompleted
                            ? completed
                            : """{"status":"ok","result":{"status":"acknowledged"}}""";
                    }

                    if (request.Method == HttpMethod.Delete && path == $"/collections/{draft}")
                    {
                        draftDeleted = true;
                        return """{"status":"ok","result":true}""";
                    }

                    if (path == $"/collections/{activeCollection}/points/scroll")
                        return publish
                            ? """{"status":"ok","result":{"points":[{"payload":{"source_file":"01-orders.txt"}},{"payload":{"source_file":"02-storage.txt"}}],"next_page_offset":null}}"""
                            : """{"status":"ok","result":{"points":[{"payload":{"source_file":"previous.txt"}}],"next_page_offset":null}}""";

                    if (path == $"/collections/{alias}/points/query")
                    {
                        body!["query"]!.AsArray().Select(value => value!.GetValue<int>()).Should().Equal(1, 0);
                        body["limit"]!.GetValue<int>().Should().Be(4);
                        body["filter"]!["must"]![0]!["match"]!["value"]!.GetValue<string>()
                            .Should().Be("text-embedding-3-small:2");
                        return JsonSerializer.Serialize(new
                        {
                            status = "ok",
                            result = new { points = new[] { new { score = 1, payload = new { content = publish ? orderGuide : previousContent } } } }
                        });
                    }

                    throw new InvalidOperationException($"Unexpected Qdrant request {request.Method} {path}");
                })) { BaseAddress = new Uri(qdrant.Endpoint) });

            KnowledgeBaseService CreateKnowledgeBase() => new(embedder, [new PlainTextLoader()],
                new AiChatOptions { KnowledgeBasePath = corpus.FullName }, openAI,
                new QdrantKnowledgeStore(httpFactory.Object, qdrant), NullLogger<KnowledgeBaseService>.Instance);

            (await CreateKnowledgeBase().BuildIndexAsync()).Should().Be(publish);
            activeCollection.Should().Be(publish ? draft : previousCollection);
            draftDeleted.Should().Be(!publish);

            // A fresh backend can search the active alias and reindex unchanged documents without new embeddings.
            var restarted = CreateKnowledgeBase();
            var status = await restarted.GetStatusAsync();
            status.IsReady.Should().BeTrue();
            status.ChunkCount.Should().Be(publish ? 2 : 1);
            (await restarted.SearchAsync("Mua lô thế nào?")).Should().Be(publish ? orderGuide : previousContent);
            if (publish)
                (await restarted.BuildIndexAsync()).Should().BeTrue();
            documentEmbeddingRequests.Should().Be(1);
        }
        finally
        {
            corpus.Delete(recursive: true);
        }
    }

    private static OpenAIEmbeddingService CreateEmbedder(Func<HttpRequestMessage, string> respond, bool useMaf = false,
        OpenAIOptions? options = null)
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("OpenAIEmbedding"))
            .Returns(() => new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://api.openai.com/v1/") });

        return new OpenAIEmbeddingService(httpFactory.Object,
            options ?? new OpenAIOptions { ApiKey = "test-key", EmbeddingDimensions = 2 },
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
