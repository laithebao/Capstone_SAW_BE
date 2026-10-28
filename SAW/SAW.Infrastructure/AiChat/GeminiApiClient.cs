using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Minimal HTTP wrapper for Google Gemini REST API (generateContent + multi-turn).
/// Supports Function Calling (Tool Use) to avoid hallucination.
/// </summary>
public sealed class GeminiApiClient(HttpClient http, GeminiOptions options, ILogger<GeminiApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // ── Tool declarations sent with every request ──────────────────────
    private static readonly JsonNode ToolsDeclaration = JsonNode.Parse("""
    [
      {
        "functionDeclarations": [
          {
            "name": "GetBatchSummary",
            "description": "Lấy thông tin tổng quan về một lô hàng nông sản theo Mã lô hàng (BatchCode) mà Nhà phân phối nhìn thấy trên hệ thống: tên sản phẩm, loại nông sản, vùng trồng, nhà cung cấp, ngày thu hoạch, hạn sử dụng, trọng lượng, điều kiện bảo quản, trạng thái lô.",
            "parameters": {
              "type": "OBJECT",
              "properties": {
                "batchCode": { "type": "STRING", "description": "Mã lô hàng (BatchCode) ví dụ: 'DEMO-DIST-LOT-01', 'PB-20261026-001'..." }
              },
              "required": ["batchCode"]
            }
          },
          {
            "name": "GetBatchQcReport",
            "description": "Lấy kết quả kiểm định chất lượng (QC) đầy đủ của một lô hàng theo Mã lô hàng (BatchCode): xếp hạng chất lượng, kết quả cảm quan (tươi, màu, độ chín, hư hỏng), kết quả phân tích hóa nghiệm (dư lượng hóa chất, vi sinh), tên tiêu chuẩn áp dụng, chi tiết từng tiêu chí.",
            "parameters": {
              "type": "OBJECT",
              "properties": {
                "batchCode": { "type": "STRING", "description": "Mã lô hàng (BatchCode) cần tra cứu kiểm định, ví dụ: 'DEMO-DIST-LOT-01'..." }
              },
              "required": ["batchCode"]
            }
          },
          {
            "name": "GetStorageAdvice",
            "description": "Lấy thông số kỹ thuật bảo quản và hạn sử dụng ước tính cho một loại nông sản cụ thể: dải nhiệt độ, độ ẩm, hạn bảo quản thông thường, lưu ý đặc biệt.",
            "parameters": {
              "type": "OBJECT",
              "properties": {
                "cropTypeId": { "type": "NUMBER", "description": "ID số của loại nông sản (CropTypeID)" }
              },
              "required": ["cropTypeId"]
            }
          },
          {
            "name": "SearchKnowledgeBase",
            "description": "Tìm kiếm thông tin trong toàn bộ kho tài liệu tri thức (Knowledge Base) của hệ thống: bao gồm cẩm nang nông sản, kiến thức đặc thù, tiêu chuẩn kiểm định, quy định phân hạng chất lượng, quy trình kho SAW, và các tài liệu PDF/Word/MD được nạp vào hệ thống. BẠN NÊN GỌI TOOL NÀY cho bất kỳ câu hỏi nào về kiến thức nông sản, nông nghiệp hoặc quy trình kho để kiểm tra xem hệ thống có tài liệu ghi nhận riêng hay không trước khi trả lời.",
            "parameters": {
              "type": "OBJECT",
              "properties": {
                "query": { "type": "STRING", "description": "Từ khóa hoặc câu hỏi cần tra cứu trong kho tri thức" }
              },
              "required": ["query"]
            }
          }
        ]
      }
    ]
    """)!;

    /// <summary>
    /// Sends a conversational turn to Gemini, handles Function Calling loop, returns final text.
    /// </summary>
    public async Task<string> ChatAsync(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> history,
        string userMessage,
        Func<string, JsonNode?, Task<string>> toolExecutor,
        CancellationToken ct)
    {
        try
        {
            return await ChatInternalAsync(systemPrompt, history, userMessage, toolExecutor, ct);
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Gemini API call bị hủy hoặc timeout (model: {Model})", options.Model);
            return "⏱️ Gemini AI không phản hồi trong thời gian cho phép. Vui lòng thử lại.";
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Lỗi kết nối đến Gemini API (model: {Model})", options.Model);
            return "🔌 Không thể kết nối đến Gemini API. Kiểm tra kết nối mạng hoặc thử lại sau.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi không mong đợi khi gọi Gemini (model: {Model})", options.Model);
            return "⚠️ Lỗi nội bộ khi xử lý yêu cầu AI. Vui lòng thử lại.";
        }
    }

    private async Task<string> ChatInternalAsync(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> history,
        string userMessage,
        Func<string, JsonNode?, Task<string>> toolExecutor,
        CancellationToken ct)
    {
        var contents = BuildContents(history, userMessage);

        // Cho phép tối đa 5 vòng tool-call (SearchKnowledgeBase + GetBatch + GetQC + ...)
        // Tool luôn được gửi trong mọi vòng — không dùng heuristic vì KB cần được gọi với bất kỳ câu hỏi nghiệp vụ nào.
        for (var round = 0; round < 5; round++)
        {
            var generationConfig = new JsonObject
            {
                ["maxOutputTokens"] = options.MaxOutputTokens,
                ["temperature"] = options.Temperature
            };

            // Gemini 2.5 / 3.x Flash Lite có thinking mặc định → giới hạn 128 tokens để giảm thời gian chờ
            if (options.Model.Contains("3.5") || options.Model.Contains("3.8") || options.Model.Contains("2.5"))
            {
                generationConfig["thinkingConfig"] = new JsonObject
                {
                    ["thinkingBudget"] = 128
                };
            }

            var requestBody = new JsonObject
            {
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
                },
                ["contents"] = contents.DeepClone(),
                ["generationConfig"] = generationConfig,
                // Luôn gửi tools — Gemini tự quyết định có gọi hay không
                ["tools"] = ToolsDeclaration.DeepClone()
            };

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{options.Model}:generateContent?key={options.ApiKey}";

            using var httpResponse = await http.PostAsJsonAsync(url, requestBody, JsonOpts, ct);
            var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Gemini API lỗi HTTP {Status} (model: '{Model}'): {Body}",
                    (int)httpResponse.StatusCode, options.Model, responseBody);

                return (int)httpResponse.StatusCode switch
                {
                    400 => "⚠️ Gemini API báo yêu cầu không hợp lệ. Kiểm tra cấu hình GeminiAI trong appsettings.",
                    401 or 403 => "⚠️ API Key Gemini không hợp lệ hoặc hết hạn. Vui lòng kiểm tra GeminiAI:ApiKey trong appsettings.Development.json.",
                    404 => $"⚠️ Model '{options.Model}' không tồn tại trên Gemini API. Vui lòng kiểm tra GeminiAI:Model trong appsettings.",
                    429 => "⚠️ Vượt quá hạn mức gọi API Gemini (quota). Vui lòng thử lại sau ít phút.",
                    500 or 503 => "⚠️ Dịch vụ Gemini AI đang tạm thời gián đoạn. Vui lòng thử lại sau.",
                    _ => $"⚠️ Gemini API trả về lỗi HTTP {(int)httpResponse.StatusCode}."
                };
            }

            JsonNode? responseJson;
            try
            {
                responseJson = JsonNode.Parse(responseBody);
            }
            catch
            {
                logger.LogError("Không thể parse JSON từ Gemini API: {Body}", responseBody);
                return "Xin lỗi, phản hồi từ AI không đọc được. Vui lòng thử lại.";
            }

            var candidate = responseJson?["candidates"]?[0];
            var parts = candidate?["content"]?["parts"];
            if (parts == null) return "Xin lỗi, tôi không thể tạo câu trả lời lúc này.";

            // Check for function calls
            var functionCallParts = parts.AsArray()
                .Where(p => p?["functionCall"] != null)
                .ToList();

            if (functionCallParts.Count > 0)
            {
                logger.LogInformation("[Gemini] Round {Round}: gọi {Count} tool(s)", round, functionCallParts.Count);

                var modelTurnParts = new JsonArray();
                var responseTurnParts = new JsonArray();

                foreach (var callPart in functionCallParts)
                {
                    var funcCall = callPart!["functionCall"]!;
                    var funcName = funcCall["name"]!.GetValue<string>();
                    var funcArgs = funcCall["args"] as JsonNode;

                    logger.LogInformation("[Gemini] Tool được gọi: {ToolName}", funcName);

                    var toolResult = await toolExecutor(funcName, funcArgs);

                    modelTurnParts.Add(callPart.DeepClone());
                    responseTurnParts.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = funcName,
                            ["response"] = new JsonObject { ["content"] = toolResult }
                        }
                    });
                }

                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelTurnParts });
                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = responseTurnParts });

                continue; // next round to synthesize
            }

            // Text response
            var textParts = parts.AsArray()
                .Where(p => p?["text"] != null)
                .Select(p => p!["text"]!.GetValue<string>());
            return string.Join("", textParts).Trim();
        }

        return "Xin lỗi, tôi không thể hoàn thành yêu cầu này lúc này.";
    }

    private static JsonArray BuildContents(IReadOnlyList<(string Role, string Content)> history, string userMessage)
    {
        var arr = new JsonArray();
        foreach (var (role, content) in history)
        {
            arr.Add(new JsonObject
            {
                ["role"] = role == "user" ? "user" : "model",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = content } }
            });
        }
        arr.Add(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray { new JsonObject { ["text"] = userMessage } }
        });
        return arr;
    }
}
