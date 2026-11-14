using System.ClientModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using SAW.Infrastructure.AiChat;

namespace SAW.Infrastructure.AiForecast;

public sealed class MafForecastClient(HttpClient http, OpenAIOptions options, ILogger<MafForecastClient> logger)
    : IForecastModelClient
{
    public async Task<(string Json, string Model)> GenerateAsync(
        string instructions, string input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model)
            || options.ForecastMaxOutputTokens <= 0)
        {
            logger.LogWarning("OpenAI dự báo hao hụt chưa được cấu hình đầy đủ.");
            throw new HttpRequestException("OpenAI forecast is not configured.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ForecastTimeoutSeconds, 1, 100)));
        using var responses = MafOpenAI.CreateResponsesClient(http, options, logger);

        var chatOptions = MafOpenAI.CreateChatOptions(options, instructions, options.ForecastMaxOutputTokens);
#pragma warning disable OPENAI001 // Set strictness explicitly in the Responses SDK request.
        chatOptions.RawRepresentationFactory = _ => new CreateResponseOptions
        {
            StoredOutputEnabled = false,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("spoilage_forecast",
                    BinaryData.FromString(ForecastOutputSchema.Value.GetRawText()), jsonSchemaIsStrict: true)
            }
        };
#pragma warning restore OPENAI001
        var agent = new ChatClientAgent(responses, new ChatClientAgentOptions
        {
            Name = "SawSpoilageForecast",
            ChatOptions = chatOptions,
            UseProvidedChatClientAsIs = true
        });

        try
        {
            await agent.RunAsync(input, cancellationToken: deadline.Token);
        }
        catch (ClientResultException ex)
        {
            logger.LogWarning("OpenAI forecast trả lỗi HTTP {Status}.", ex.Status);
            throw new HttpRequestException("OpenAI forecast request failed.");
        }

        var json = responses.Text;
        if (responses.Refusal is not null || string.IsNullOrWhiteSpace(json) || responses.HasToolCalls)
            throw new JsonException("OpenAI forecast did not return usable structured output.");

        return (json, string.IsNullOrWhiteSpace(responses.Model) ? options.Model : responses.Model);
    }
}
