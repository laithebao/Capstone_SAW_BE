using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiForecast;
using SAW.Infrastructure.Extensions;
using Xunit;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class MafOpenAIIntegrationTests
{
    private static readonly OpenAIOptions Options = new() { ApiKey = "test-key" };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Infrastructure_ResolvesEachAiBackendIndependently(bool chat, bool forecast)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiExecution:UseMafForChat"] = chat.ToString(),
            ["AiExecution:UseMafForForecast"] = forecast.ToString()
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration).BuildServiceProvider();
        using var scope = services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAiChatModelClient>().Should()
            .BeOfType(chat ? typeof(MafAiChatClient) : typeof(OpenAIChatClient));
        scope.ServiceProvider.GetRequiredService<IForecastModelClient>().Should()
            .BeOfType(forecast ? typeof(MafForecastClient) : typeof(OpenAIForecastClient));
    }

    [Fact]
    public async Task Chat_ReplaysToolResultAndReasoningThroughResponsesApi()
    {
        using var handler = new ResponsesHandler(
            Response("""
                [
                  {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque"},
                  {"type":"function_call","id":"fc_1","call_id":"call_qc","status":"completed",
                   "name":"GetBatchQcReport","arguments":"{\"batchCode\":\"LOT-01\"}"}
                ]
                """, responseId: "resp_tool"),
            TextResponse("Lô LOT-01 đã đạt QC."));
        using var http = new HttpClient(handler);
        var client = new MafAiChatClient(http, Options, NullLogger<MafAiChatClient>.Instance);
        var toolCalls = 0;

        var response = await client.ChatAsync("Quy tắc SAW.", [("assistant", "Chào bạn.")], "Kiểm tra LOT-01.", 5,
            (name, args, _) =>
            {
                toolCalls++;
                name.Should().Be("GetBatchQcReport");
                args["batchCode"]!.GetValue<string>().Should().Be("LOT-01");
                return Task.FromResult("""{"qcResult":"PASS"}""");
            }, CancellationToken.None);

        response.IsError.Should().BeFalse("the SDK completed {0} model requests and executed {1} tools", handler.Requests.Count, toolCalls);
        response.Reply.Should().Be("Lô LOT-01 đã đạt QC.");
        toolCalls.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
        var initial = handler.Requests[0];
        initial["max_output_tokens"]!.GetValue<int>().Should().Be(2048);
        initial["parallel_tool_calls"]!.GetValue<bool>().Should().BeFalse();
        initial["input"]!.AsArray().Should().Contain(item => item!["role"]!.GetValue<string>() == "assistant");
        var tools = initial["tools"]!.AsArray();
        tools.Select(item => item!["name"]!.GetValue<string>()).Should()
            .BeEquivalentTo("GetBatchSummary", "GetBatchQcReport", "GetStorageAdvice", "SearchKnowledgeBase");
        foreach (var tool in tools)
        {
            tool!["strict"]!.GetValue<bool>().Should().BeTrue();
            tool["parameters"]!["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
            tool["parameters"]!["properties"]!.AsObject().Count.Should().Be(1);
        }

        var replay = handler.Requests[1]["input"]!.AsArray();
        replay.Single(item => item?["type"]?.GetValue<string>() == "reasoning")!
            ["encrypted_content"]!.GetValue<string>().Should().Be("opaque");
        var output = replay.Single(item => item?["type"]?.GetValue<string>() == "function_call_output")!;
        output["call_id"]!.GetValue<string>().Should().Be("call_qc");
        output["output"]!.GetValue<string>().Should().Be("""{"qcResult":"PASS"}""");
    }

    [Fact]
    public async Task Chat_StopsWithoutExecutingAToolWhenNoModelRoundRemains()
    {
        using var handler = new ResponsesHandler(Response("""
            [{"type":"function_call","id":"fc_1","call_id":"call_kb","status":"completed",
              "name":"SearchKnowledgeBase","arguments":"{\"query\":\"SAW\"}"}]
            """));
        using var http = new HttpClient(handler);
        var client = new MafAiChatClient(http, Options, NullLogger<MafAiChatClient>.Instance);

        var result = await client.ChatAsync("Quy tắc SAW.", [], "Quy trình SAW?", 1,
            (_, _, _) => throw new InvalidOperationException("No tool should run."), CancellationToken.None);

        result.IsError.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Chat_DoesNotExecuteTheSameToolCallTwice()
    {
        const string call = """
            [{"type":"function_call","id":"fc_1","call_id":"call_kb","status":"completed",
              "name":"SearchKnowledgeBase","arguments":"{\"query\":\"SAW\"}"}]
            """;
        using var handler = new ResponsesHandler(Response(call, responseId: "resp_first"),
            Response(call, responseId: "resp_second"));
        using var http = new HttpClient(handler);
        var client = new MafAiChatClient(http, Options, NullLogger<MafAiChatClient>.Instance);
        var toolCalls = 0;

        var result = await client.ChatAsync("Quy tắc SAW.", [], "Quy trình SAW?", 5,
            (_, _, _) =>
            {
                toolCalls++;
                return Task.FromResult("Quy trình mua lô hàng.");
            }, CancellationToken.None);

        result.IsError.Should().BeTrue();
        toolCalls.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("insufficient_quota", 1)]
    [InlineData("rate_limit_exceeded", 2)]
    public async Task Chat_RetriesTemporaryRateLimitsWithoutRetryingQuotaErrors(string code, int expectedRequests)
    {
        using var handler = new RateLimitHandler(code);
        using var http = new HttpClient(handler);
        var client = new MafAiChatClient(http, Options, NullLogger<MafAiChatClient>.Instance);

        var result = await client.ChatAsync("Quy tắc SAW.", [], "Xin chào.", 5,
            (_, _, _) => throw new InvalidOperationException("No tool should run."), CancellationToken.None);

        result.IsError.Should().Be(expectedRequests == 1);
        handler.RequestCount.Should().Be(expectedRequests);
    }

    [Fact]
    public async Task Forecast_RequestsStrictSchemaInOneResponse()
    {
        const string json = """{"spoilageRiskScore":60,"riskLevel":"HIGH","recommendation":"Kiểm tra nhiệt độ."}""";
        using var handler = new ResponsesHandler(TextResponse(json));
        using var http = new HttpClient(handler);
        var client = new MafForecastClient(http, Options, NullLogger<MafForecastClient>.Instance);

        var result = await client.GenerateAsync("Quy tắc dự báo.", "Dữ liệu kiểm thử.", CancellationToken.None);

        result.Json.Should().Be(json);
        result.Model.Should().Be("gpt-6-luna-snapshot");
        var payload = handler.Requests.Should().ContainSingle().Subject;
        payload["max_output_tokens"]!.GetValue<int>().Should().Be(4096);
        var format = payload["text"]!["format"]!;
        format["type"]!.GetValue<string>().Should().Be("json_schema");
        format["strict"]!.GetValue<bool>().Should().BeTrue();
        format["schema"]!["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .Should().Contain(["spoilageRiskScore", "recommendation", "actions"]);
    }

    [Theory]
    [InlineData("incomplete", """[{"type":"output_text","text":"{}","annotations":[]}]""")]
    [InlineData("completed", """[{"type":"refusal","refusal":"Không thể."}]""")]
    public async Task Forecast_RejectsIncompleteResponseAndRefusal(string status, string content)
    {
        using var handler = new ResponsesHandler(Response(
            $$"""[{"type":"message","id":"msg_1","role":"assistant","status":"{{status}}","content":{{content}}}]""", status));
        using var http = new HttpClient(handler);
        var client = new MafForecastClient(http, Options, NullLogger<MafForecastClient>.Instance);

        var run = () => client.GenerateAsync("Quy tắc dự báo.", "Dữ liệu kiểm thử.", CancellationToken.None);

        await run.Should().ThrowAsync<JsonException>();
    }

    private static string TextResponse(string text) => Response($$"""
        [{"type":"message","id":"msg_1","role":"assistant","status":"completed",
          "content":[{"type":"output_text","text":{{JsonSerializer.Serialize(text)}},"annotations":[]}]}]
        """);

    private static string Response(string output, string status = "completed", string responseId = "resp_final") => $$"""
        {"id":"{{responseId}}","object":"response","created_at":1794000000,"status":"{{status}}",
         "model":"gpt-6-luna-snapshot","output":{{output}}}
        """;

    private sealed class ResponsesHandler(params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> _replies = new(replies);
        public List<JsonObject> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri.Should().Be(new Uri("https://api.openai.com/v1/responses"));
            request.Headers.Authorization!.Parameter.Should().Be("test-key");
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            payload["store"]!.GetValue<bool>().Should().BeFalse();
            payload["reasoning"]!["effort"]!.GetValue<string>().Should().Be("low");
            payload["previous_response_id"].Should().BeNull();
            Requests.Add(payload);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_replies.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RateLimitHandler(string errorCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = ++RequestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = errorCode } }),
                        Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TextResponse("Xin chào."), Encoding.UTF8, "application/json")
                };
            return Task.FromResult(response);
        }
    }
}
