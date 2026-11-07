using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiForecast;
using Xunit;

namespace SAW.Test.Infrastructure.AiForecast;

public sealed class OpenAIForecastClientTests
{
    [Fact]
    public async Task GenerateAsync_RequestsStrictSchemaAndReturnsForecastJsonAndModel()
    {
        const string forecast = """{"spoilageRiskScore":65,"riskLevel":"HIGH","recommendation":"Kiểm tra nhiệt độ."}""";
        using var http = CreateHttpClient(request =>
        {
            request.RequestUri.Should().Be(new Uri("https://api.openai.com/v1/responses"));
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("test-key");

            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            body["model"]!.GetValue<string>().Should().Be("gpt-6-luna");
            body["store"]!.GetValue<bool>().Should().BeFalse();
            body["max_output_tokens"]!.GetValue<int>().Should().Be(4096);
            body["instructions"]!.GetValue<string>().Should().Be("Quy tắc dự báo");
            body["input"]!.GetValue<string>().Should().Be("Dữ liệu lô kiểm thử");
            var format = body["text"]!["format"]!;
            format["type"]!.GetValue<string>().Should().Be("json_schema");
            format["strict"]!.GetValue<bool>().Should().BeTrue();
            var schema = format["schema"]!;
            schema["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
            schema["required"]!.AsArray().Select(node => node!.GetValue<string>())
                .Should().Contain(["spoilageRiskScore", "recommendation", "actions"]);
            schema["properties"]!["actions"]!["items"]!["additionalProperties"]!.GetValue<bool>().Should().BeFalse();

            return $$"""
                {"status":"completed","model":"gpt-6-luna-snapshot","output":[
                    {"type":"reasoning"},
                    {"type":"message","content":[{"type":"output_text","text":{{JsonSerializer.Serialize(forecast)}}}]}
                ]}
                """;
        });

        var result = await CreateClient(http).GenerateAsync("Quy tắc dự báo", "Dữ liệu lô kiểm thử", CancellationToken.None);

        result.Json.Should().Be(forecast);
        result.Model.Should().Be("gpt-6-luna-snapshot");
    }

    [Theory]
    [InlineData("""{"status":"incomplete","output":[{"type":"message","content":[{"type":"output_text","text":"{}"}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"Không thể."}]}]}""")]
    [InlineData("""{"status":"completed","output":[]}""")]
    public async Task GenerateAsync_RejectsResponsesWithoutCompleteForecast(string response)
    {
        using var http = CreateHttpClient(_ => response);
        var client = CreateClient(http);

        var generate = () => client.GenerateAsync("Quy tắc dự báo", "Dữ liệu lô kiểm thử", CancellationToken.None);

        await generate.Should().ThrowAsync<JsonException>();
    }

    private static OpenAIForecastClient CreateClient(HttpClient http) =>
        new(http, new OpenAIOptions { ApiKey = "test-key" }, NullLogger<OpenAIForecastClient>.Instance);

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, string> respond) =>
        new(new StubHandler(respond)) { BaseAddress = new Uri("https://api.openai.com/v1/") };

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
