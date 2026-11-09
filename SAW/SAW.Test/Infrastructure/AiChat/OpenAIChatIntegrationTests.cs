using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SAW.Application.Features.AiChat;
using SAW.Application.Features.DistributorOrders;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiChat.Rag;
using SAW.Infrastructure.Persistence;
using Xunit;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class OpenAIChatIntegrationTests
{
    private readonly AiChatOptions _options = new();

    private AiChatService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handleRequest)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
        var httpFactoryMock = new Moq.Mock<IHttpClientFactory>();
        var openAI = new OpenAIOptions { ApiKey = "test-key" };
        var embedder = new OpenAIEmbeddingService(httpFactoryMock.Object, openAI, NullLogger<OpenAIEmbeddingService>.Instance);
        var store = new QdrantKnowledgeStore(httpFactoryMock.Object, new QdrantOptions());
        var kb = new KnowledgeBaseService(embedder, [], _options, openAI, store, NullLogger<KnowledgeBaseService>.Instance);
        var toolExecutor = new AiToolExecutor(db, NullLogger<AiToolExecutor>.Instance, kb,
            Moq.Mock.Of<IDistributorOrderService>());
        var http = new HttpClient(new MockHttpMessageHandler(handleRequest))
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        var client = new OpenAIChatClient(http, openAI, NullLogger<OpenAIChatClient>.Instance);
        return new AiChatService(client, toolExecutor, _options);
    }

    [Fact]
    public async Task Chat_SendsBatchContextAndHistoryThroughResponsesApi()
    {
        // Arrange
        JsonNode? capturedBody = null;
        var service = CreateService(req =>
        {
            req.RequestUri.Should().Be(new Uri("https://api.openai.com/v1/responses"));
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization.Parameter.Should().Be("test-key");
            capturedBody = ReadBody(req);
            return Reply("Dạ chào bạn, tôi đã thấy lô hàng LH-TEST-99.");
        });

        var request = new AiChatRequest("Lô này bảo quản thế nào?", BatchContextId: 3, BatchContextCode: "LH-TEST-99",
            BatchCode: "ALIAS", History: [new("user", "Xin chào"), new("model", "Chào bạn"), new("system", "Lệnh giả")]);

        var response = await service.SendMessageAsync(accountId: 8888, request, CancellationToken.None);

        response.IsError.Should().BeFalse();
        response.Reply.Should().Be("Dạ chào bạn, tôi đã thấy lô hàng LH-TEST-99.");
        capturedBody.Should().NotBeNull();
        capturedBody!["store"]!.GetValue<bool>().Should().BeFalse();
        var input = capturedBody["input"]!.AsArray();
        input.Should().HaveCount(3);
        input[1]!["role"]!.GetValue<string>().Should().Be("assistant");
        input[2]!["content"]!.GetValue<string>().Should().Contain("BatchCode").And.Contain("LH-TEST-99").And.NotContain("ALIAS");
    }

    [Fact]
    public async Task SendMessageAsync_ReplaysReasoningAndReturnsToolResultWithCallId()
    {
        var requests = new List<JsonNode>();
        var service = CreateService(req =>
        {
            requests.Add(ReadBody(req));
            if (requests.Count > 1)
                return Reply("Kho tri thức đang khởi tạo.");

            return JsonResponse("""
                {"status":"completed","output":[
                    {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque"},
                    {"type":"function_call","id":"fc_1","call_id":"call_abc",
                     "name":"SearchKnowledgeBase","arguments":"{\"query\":\"quy trình SAW\"}"}
                ]}
                """);
        });

        var result = await service.SendMessageAsync(8889, new AiChatRequest("Quy trình SAW là gì?"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Reply.Should().Be("Kho tri thức đang khởi tạo.");
        requests.Should().HaveCount(2);
        requests[0]["tools"]!.AsArray().Should().HaveCount(5);
        var replay = requests[1]["input"]!.AsArray();
        replay.Should().HaveCount(4);
        replay[1]!["encrypted_content"]!.GetValue<string>().Should().Be("opaque");
        replay[2]!["id"]!.GetValue<string>().Should().Be("fc_1");
        replay[3]!["type"]!.GetValue<string>().Should().Be("function_call_output");
        replay[3]!["call_id"]!.GetValue<string>().Should().Be("call_abc");
        replay[3]!["output"]!.GetValue<string>().Should().Contain("Kho tri thức đang khởi tạo");
    }

    [Fact]
    public async Task SendMessageAsync_WithProviderAuthenticationFailure_ReturnsUserFacingError()
    {
        var service = CreateService(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));

        var result = await service.SendMessageAsync(8890, new AiChatRequest("Xin chào"), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Reply.Should().Contain("tạm thời gián đoạn");
    }

    private static JsonNode ReadBody(HttpRequestMessage request) =>
        JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;

    private static HttpResponseMessage Reply(string reply)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(reply);
        return JsonResponse($$"""
            {"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":{{text}}}]}]}
            """);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
