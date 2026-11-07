using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Responses;

namespace SAW.Infrastructure.AiChat;

internal static class MafOpenAI
{
    internal static OpenAIClient CreateClient(HttpClient http, OpenAIOptions options) =>
        new(new ApiKeyCredential(options.ApiKey), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new OpenAIRetryPolicy()
        });

    internal static ChatOptions CreateChatOptions(OpenAIOptions options, string instructions, int maxOutputTokens)
    {
        var chatOptions = new ChatOptions { Instructions = instructions, MaxOutputTokens = maxOutputTokens };
        var configuredEffort = options.ReasoningEffort?.Trim();
        if (string.Equals(configuredEffort, "xhigh", StringComparison.OrdinalIgnoreCase))
            configuredEffort = nameof(ReasoningEffort.ExtraHigh);

        if (Enum.TryParse<ReasoningEffort>(configuredEffort, ignoreCase: true, out var effort)
            && Enum.IsDefined(effort))
            chatOptions.Reasoning = new ReasoningOptions { Effort = effort };

        return chatOptions;
    }

#pragma warning disable OPENAI001, MAAI001 // Responses SDK and stateless adapter are experimental.
    internal static MafResponseClient CreateResponsesClient(
        HttpClient http, OpenAIOptions options, ILogger logger, int maxRequests = 1) =>
        new(CreateClient(http, options).GetResponsesClient().AsIChatClientWithStoredOutputDisabled(options.Model), logger, maxRequests);
#pragma warning restore OPENAI001, MAAI001

    private sealed class OpenAIRetryPolicy() : ClientRetryPolicy(maxRetries: 1)
    {
        protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception)
        {
            var response = message.Response;
            if (response is null || response.Status == 0)
                return exception is HttpRequestException || await base.ShouldRetryAsync(message, exception);

            if (response.Headers.TryGetValue("Retry-After", out var value)
                && RetryConditionHeaderValue.TryParse(value, out var retryAfter)
                && (retryAfter.Delta ?? retryAfter.Date - DateTimeOffset.UtcNow) > TimeSpan.FromSeconds(5))
                return false;

            if (response.Status != 429)
                return response.Status >= 500;

            try
            {
                var body = await response.BufferContentAsync(message.CancellationToken);
                using var json = JsonDocument.Parse(body.ToMemory());
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    return !IsQuotaError(error, "code") && !IsQuotaError(error, "type");
            }
            catch (JsonException)
            {
                // A 429 without a recognized quota code can be transient.
            }

            return true;
        }

        private static bool IsQuotaError(JsonElement error, string property) =>
            error.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is "insufficient_quota" or "billing_hard_limit_reached";
    }
}

internal sealed class MafResponseClient(IChatClient inner, ILogger logger, int maxRequests) : DelegatingChatClient(inner)
{
    internal int RequestCount { get; private set; }
    internal string? Text { get; private set; }
    internal string? Model { get; private set; }
    internal bool HasToolCalls { get; private set; }
    internal bool IsIncomplete { get; private set; }
    internal string? Refusal { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (RequestCount >= maxRequests)
            throw new JsonException("OpenAI model request budget exhausted.");

        RequestCount++;
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

#pragma warning disable OPENAI001 // Preserve provider status and refusal information on the Responses API.
        var result = response.RawRepresentation as ResponseResult;
        IsIncomplete = result?.Status == ResponseStatus.Incomplete;
        if (result is null || result.Status != ResponseStatus.Completed)
            throw new JsonException("OpenAI did not complete the response.");

        Refusal = result.OutputItems.OfType<MessageResponseItem>()
            .SelectMany(item => item.Content).Select(part => part.Refusal)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
#pragma warning restore OPENAI001

        // The function-invoking client may append previous messages to the response after this call.
        Text = response.Text;
        Model = response.ModelId;
        HasToolCalls = response.Messages.SelectMany(message => message.Contents).Any(content => content is FunctionCallContent);
        logger.LogDebug("OpenAI Responses {ResponseId}, model {Model}, input tokens {InputTokens}, output tokens {OutputTokens}.",
            response.ResponseId, response.ModelId, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
        return response;
    }
}
