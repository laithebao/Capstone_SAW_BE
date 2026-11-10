using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.AiChat;
using static SAW.Infrastructure.AiChat.OpenAIHttp;

namespace SAW.Infrastructure.AiChat;

public sealed class OpenAIChatClient(HttpClient http, OpenAIOptions options, ILogger<OpenAIChatClient> logger) : IAiChatModelClient
{
    private const string Unavailable = "Trợ lý AI đang tạm thời gián đoạn. Vui lòng thử lại sau.";
    private const int MaxToolCallsPerTurn = 5;
    private const int MaxToolOutputChars = 24000;

    private static readonly JsonArray Tools = JsonNode.Parse("""
    [
      {
        "name": "GetBatchSummary",
        "description": "Lấy thông tin tổng quan về một lô hàng nông sản theo Mã lô hàng (BatchCode) mà Nhà phân phối nhìn thấy trên hệ thống: tên sản phẩm, loại nông sản, vùng trồng, nhà cung cấp, ngày thu hoạch, hạn sử dụng, trọng lượng, điều kiện bảo quản, trạng thái lô.",
        "parameters": {
          "type": "object",
          "properties": {
            "batchCode": {
              "type": "string",
              "description": "Mã lô hàng (BatchCode) ví dụ: 'DEMO-DIST-LOT-01', 'PB-20261026-001'..."
            }
          },
          "required": [
            "batchCode"
          ],
          "additionalProperties": false
        },
        "type": "function",
        "strict": true
      },
      {
        "name": "GetBatchQcReport",
        "description": "Lấy kết quả kiểm định chất lượng (QC) đầy đủ của một lô hàng theo Mã lô hàng (BatchCode): xếp hạng chất lượng, kết quả cảm quan (tươi, màu, độ chín, hư hỏng), kết quả phân tích hóa nghiệm (dư lượng hóa chất, vi sinh), tên tiêu chuẩn áp dụng, chi tiết từng tiêu chí.",
        "parameters": {
          "type": "object",
          "properties": {
            "batchCode": {
              "type": "string",
              "description": "Mã lô hàng (BatchCode) cần tra cứu kiểm định, ví dụ: 'DEMO-DIST-LOT-01'..."
            }
          },
          "required": [
            "batchCode"
          ],
          "additionalProperties": false
        },
        "type": "function",
        "strict": true
      },
      {
        "name": "SearchSaleLots",
        "description": "Tra cứu danh mục lô nông sản đang mở bán trên SAW. Dùng để liệt kê, tìm theo nông sản hoặc mã lô, đếm lô và so sánh giá cả lô. Sắp xếp toàn bộ kết quả phù hợp trước khi phân trang.",
        "parameters": {
          "type": "object",
          "properties": {
            "search": { "type": "string", "description": "Tên nông sản, tên sản phẩm hoặc mã lô. Dùng chuỗi rỗng để tìm trong toàn bộ danh mục." },
            "sort": { "type": "string", "enum": ["newest", "price_asc", "price_desc"], "description": "Lô mới cập nhật, giá cả lô tăng dần hoặc giá cả lô giảm dần." },
            "page": { "type": "integer", "description": "Số trang bắt đầu từ 1. Mỗi trang có tối đa 10 lô." }
          },
          "required": ["search", "sort", "page"],
          "additionalProperties": false
        },
        "type": "function",
        "strict": true
      },
      {
        "name": "GetStorageAdvice",
        "description": "Lấy thông số kỹ thuật bảo quản và hạn sử dụng ước tính cho một loại nông sản cụ thể: dải nhiệt độ, độ ẩm, hạn bảo quản thông thường, lưu ý đặc biệt.",
        "parameters": {
          "type": "object",
          "properties": {
            "cropTypeId": {
              "type": "integer",
              "description": "ID số của loại nông sản (CropTypeID)"
            }
          },
          "required": [
            "cropTypeId"
          ],
          "additionalProperties": false
        },
        "type": "function",
        "strict": true
      },
      {
        "name": "SearchKnowledgeBase",
        "description": "Tìm kiếm thông tin trong toàn bộ kho tài liệu tri thức (Knowledge Base) của hệ thống: bao gồm cẩm nang nông sản, kiến thức đặc thù, tiêu chuẩn kiểm định, quy định phân hạng chất lượng, quy trình kho SAW, và các tài liệu PDF/Word/MD được nạp vào hệ thống. BẠN NÊN GỌI TOOL NÀY cho bất kỳ câu hỏi nào về kiến thức nông sản, nông nghiệp hoặc quy trình kho để kiểm tra xem hệ thống có tài liệu ghi nhận riêng hay không trước khi trả lời.",
        "parameters": {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Từ khóa hoặc câu hỏi cần tra cứu trong kho tri thức"
            }
          },
          "required": [
            "query"
          ],
          "additionalProperties": false
        },
        "type": "function",
        "strict": true
      }
    ]
    """)!.AsArray();

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
            || options.MaxOutputTokens <= 0)
        {
            logger.LogWarning("OpenAI chat chưa được cấu hình đầy đủ.");
            return new AiChatResponse(Unavailable, IsError: true);
        }

        var input = new JsonArray();
        foreach (var (role, content) in history)
            input.Add(Message(role, content));
        input.Add(Message("user", userMessage));

        var seenCalls = new HashSet<string>(StringComparer.Ordinal);
        var toolOutputChars = 0;

        try
        {
            for (var round = 1; round <= maxRequests; round++)
            {
                var payload = new JsonObject
                {
                    ["model"] = options.Model,
                    ["instructions"] = instructions,
                    ["input"] = input.DeepClone(),
                    ["tools"] = Tools.DeepClone(),
                    ["tool_choice"] = "auto",
                    ["parallel_tool_calls"] = false,
                    ["max_output_tokens"] = options.MaxOutputTokens,
                    ["store"] = false
                };
                if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
                    payload["reasoning"] = new JsonObject { ["effort"] = options.ReasoningEffort };

                var response = await OpenAIHttp.PostAsync(http, "responses", payload, options.ApiKey, logger, ct);
                var status = ReadString(response["status"]);
                if (status == "incomplete")
                    return new AiChatResponse("Trợ lý AI chưa hoàn tất câu trả lời. Vui lòng thử câu hỏi ngắn hơn.", IsError: true);
                if (status != "completed" || response["output"] is not JsonArray output)
                    break;

                var calls = output.OfType<JsonObject>()
                    .Where(item => ReadString(item["type"]) == "function_call").ToArray();
                if (calls.Length == 0)
                {
                    var reply = ReadReply(output);
                    if (reply.Length > 0)
                        return new AiChatResponse(reply);
                    break;
                }

                if (round == maxRequests || seenCalls.Count + calls.Length > MaxToolCallsPerTurn)
                    break;

                // Replay the provider items in order, including stateless reasoning and call IDs.
                foreach (var item in output)
                    input.Add(item?.DeepClone());

                foreach (var call in calls)
                {
                    var callId = ReadString(call["call_id"]);
                    if (string.IsNullOrWhiteSpace(callId) || !seenCalls.Add(callId))
                        return new AiChatResponse(Unavailable, IsError: true);

                    var name = ReadString(call["name"]);
                    var arguments = ReadArguments(ReadString(call["arguments"]));
                    var result = """{"error":"Tham số công cụ không hợp lệ hoặc công cụ không được hỗ trợ."}""";
                    if (name is "GetBatchSummary" or "GetBatchQcReport" or "SearchSaleLots" or "GetStorageAdvice" or "SearchKnowledgeBase"
                        && arguments is not null)
                    {
                        logger.LogInformation("OpenAI tool {Tool}, vòng {Round}.", name, round);
                        result = await executeTool(name, arguments, ct);
                    }

                    if (result.Length > MaxToolOutputChars - toolOutputChars)
                        return new AiChatResponse("Dữ liệu cần tra cứu quá lớn. Vui lòng thu hẹp câu hỏi.", IsError: true);
                    toolOutputChars += result.Length;

                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = callId,
                        ["output"] = result
                    });
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiChatResponse("Trợ lý AI phản hồi chậm. Vui lòng thử lại.", IsError: true);
        }
        catch (HttpRequestException ex)
        {
            var message = ex.StatusCode == HttpStatusCode.TooManyRequests
                ? "Trợ lý AI đang vượt hạn mức xử lý. Vui lòng thử lại sau."
                : Unavailable;
            return new AiChatResponse(message, IsError: true);
        }
        catch (JsonException)
        {
            logger.LogWarning("OpenAI trả phản hồi chat không đúng định dạng.");
        }

        return new AiChatResponse(Unavailable, IsError: true);
    }

    private static JsonObject Message(string role, string content) =>
        new() { ["role"] = role, ["content"] = content };

    private static JsonObject? ReadArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments.Length > 4000)
            return null;

        try
        {
            return JsonNode.Parse(arguments) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadReply(JsonArray output)
    {
        var parts = ReadMessageParts(output)
            .Select(part => ReadString(part["type"]) switch
            {
                "output_text" => ReadString(part["text"]),
                "refusal" => ReadString(part["refusal"]),
                _ => null
            });

        return string.Concat(parts).Trim();
    }
}
