using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.AiChat;
using AgentMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SAW.Infrastructure.AiChat;

public sealed class MafAiChatClient(HttpClient http, OpenAIOptions options, ILogger<MafAiChatClient> logger)
    : IAiChatModelClient
{
    private const string Unavailable = "Trợ lý AI đang tạm thời gián đoạn. Vui lòng thử lại sau.";
    private const int MaxToolCallsPerTurn = 5;
    private const int MaxToolOutputChars = 24000;

    public async Task<AiChatResponse> ChatAsync(
        string instructions,
        IReadOnlyList<(string Role, string Content)> history,
        string userMessage,
        int maxRequests,
        Func<string, JsonObject, CancellationToken, Task<string>> executeTool,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model)
            || options.MaxOutputTokens <= 0 || maxRequests <= 0)
        {
            logger.LogWarning("OpenAI chat chưa được cấu hình đầy đủ.");
            return new AiChatResponse(Unavailable, IsError: true);
        }

        var responses = MafOpenAI.CreateResponsesClient(http, options, logger, maxRequests);
        var seenCalls = new HashSet<string>(StringComparer.Ordinal);
        var toolOutputChars = 0;
        string? error = null;

        async ValueTask<object?> InvokeToolAsync(FunctionInvocationContext context, CancellationToken token)
        {
            var callId = context.CallContent.CallId;
            if (responses.RequestCount >= maxRequests || seenCalls.Count >= MaxToolCallsPerTurn
                || string.IsNullOrWhiteSpace(callId) || !seenCalls.Add(callId))
            {
                error = Unavailable;
                context.Terminate = true;
                return error;
            }

            logger.LogInformation("OpenAI tool {Tool}, vòng {Round}.", context.Function.Name, responses.RequestCount);
            var result = await context.Function.InvokeAsync(context.Arguments, token);
            var output = result is JsonElement { ValueKind: JsonValueKind.String } value
                ? value.GetString() : result as string;
            if (output is null || output.Length > MaxToolOutputChars - toolOutputChars)
            {
                error = "Dữ liệu cần tra cứu quá lớn. Vui lòng thu hẹp câu hỏi.";
                context.Terminate = true;
                return error;
            }

            toolOutputChars += output.Length;
            return output;
        }

        using var toolsClient = new FunctionInvokingChatClient(responses)
        {
            AllowConcurrentInvocation = false,
            IncludeDetailedErrors = false,
            MaximumIterationsPerRequest = maxRequests,
            TerminateOnUnknownCalls = true,
            FunctionInvoker = InvokeToolAsync
        };

        var chatOptions = MafOpenAI.CreateChatOptions(options, instructions, options.MaxOutputTokens);
        chatOptions.AllowMultipleToolCalls = false;
        chatOptions.Tools = CreateTools(executeTool);
        var agent = new ChatClientAgent(toolsClient, new ChatClientAgentOptions
        {
            Name = "SawAgriCopilot",
            ChatOptions = chatOptions,
            UseProvidedChatClientAsIs = true
        });

        var messages = history.Select(item => new AgentMessage(
            item.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, item.Content)).ToList();
        messages.Add(new AgentMessage(ChatRole.User, userMessage));

        try
        {
            await agent.RunAsync(messages, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();

            if (error is not null)
                return new AiChatResponse(error, IsError: true);

            var reply = (responses.Refusal ?? responses.Text)?.Trim();
            if (!responses.HasToolCalls && !string.IsNullOrEmpty(reply))
                return new AiChatResponse(reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiChatResponse("Trợ lý AI phản hồi chậm. Vui lòng thử lại.", IsError: true);
        }
        catch (ClientResultException ex)
        {
            logger.LogWarning("OpenAI chat trả lỗi HTTP {Status}.", ex.Status);
            var message = ex.Status == 429
                ? "Trợ lý AI đang vượt hạn mức xử lý. Vui lòng thử lại sau."
                : Unavailable;
            return new AiChatResponse(message, IsError: true);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Không thể kết nối OpenAI chat.");
        }
        catch (JsonException)
        {
            logger.LogWarning("OpenAI trả phản hồi chat không đúng định dạng hoặc chưa hoàn tất.");
            if (responses.IsIncomplete)
                return new AiChatResponse("Trợ lý AI chưa hoàn tất câu trả lời. Vui lòng thử câu hỏi ngắn hơn.", IsError: true);
        }

        return new AiChatResponse(Unavailable, IsError: true);
    }

    private static IList<AITool> CreateTools(Func<string, JsonObject, CancellationToken, Task<string>> executeTool)
    {
        Task<string> GetBatchSummary(string batchCode, CancellationToken ct) =>
            executeTool(nameof(GetBatchSummary), new JsonObject { ["batchCode"] = batchCode }, ct);
        Task<string> GetBatchQcReport(string batchCode, CancellationToken ct) =>
            executeTool(nameof(GetBatchQcReport), new JsonObject { ["batchCode"] = batchCode }, ct);
        Task<string> SearchSaleLots(
            [Description("Tên nông sản, tên sản phẩm hoặc mã lô. Dùng chuỗi rỗng để tìm trong toàn bộ danh mục.")] string search,
            [Description("newest: mới cập nhật; price_asc: giá cả lô tăng dần; price_desc: giá cả lô giảm dần.")] string sort,
            [Description("Số trang bắt đầu từ 1. Mỗi trang có tối đa 10 lô.")] int page,
            CancellationToken ct) => executeTool(nameof(SearchSaleLots),
                new JsonObject { ["search"] = search, ["sort"] = sort, ["page"] = page }, ct);
        Task<string> GetStorageAdvice(int cropTypeId, CancellationToken ct) =>
            executeTool(nameof(GetStorageAdvice), new JsonObject { ["cropTypeId"] = cropTypeId }, ct);
        Task<string> SearchKnowledgeBase(string query, CancellationToken ct) =>
            executeTool(nameof(SearchKnowledgeBase), new JsonObject { ["query"] = query }, ct);

        return [
            Tool(GetBatchSummary, nameof(GetBatchSummary),
                "Tra cứu thông tin tổng quan thực tế của lô theo Mã lô (BatchCode), gồm nguồn gốc, ngày thu hoạch, hạn dùng, trạng thái và bảo quản."),
            Tool(GetBatchQcReport, nameof(GetBatchQcReport),
                "Tra cứu báo cáo kiểm định đã hoàn tất theo Mã lô (BatchCode), gồm hạng chất lượng, cảm quan, hóa nghiệm và tiêu chí."),
            Tool(SearchSaleLots, nameof(SearchSaleLots),
                "Tra cứu danh mục lô nông sản đang mở bán trên SAW: liệt kê, tìm theo nông sản hoặc mã lô, đếm lô và so sánh giá cả lô. Sắp xếp toàn bộ kết quả phù hợp trước khi phân trang."),
            Tool(GetStorageAdvice, nameof(GetStorageAdvice),
                "Tra cứu thông số nhiệt độ, độ ẩm và hạn bảo quản theo ID của loại nông sản."),
            Tool(SearchKnowledgeBase, nameof(SearchKnowledgeBase),
                "Tìm tài liệu trong kho tri thức SAW. Dùng khi hỏi về nông sản, tiêu chuẩn, bảo quản, phân hạng hoặc quy trình SAW.")
        ];
    }

    private static AIFunction Tool(Delegate method, string name, string description) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            AdditionalProperties = new Dictionary<string, object?> { ["strict"] = true }
        });
}
