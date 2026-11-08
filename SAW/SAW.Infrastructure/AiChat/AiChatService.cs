using System.Collections.Concurrent;
using SAW.Application.Features.AiChat;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Orchestrates the full AI chat flow:
/// 1. Rate limiting (per account, in-memory)
/// 2. Safety checks (session length)
/// 3. Calls GeminiApiClient with tools
/// 4. Executes tool calls via AiToolExecutor
/// 5. Returns final text response
/// </summary>
public sealed class AiChatService(
    GeminiApiClient geminiClient,
    AiToolExecutor toolExecutor,
    GeminiOptions options) : IAiChatService
{
    // ── In-memory rate limiter (per accountId) ─────────────────────────
    private static readonly ConcurrentDictionary<int, (int Count, DateTime WindowStart)> _rateLimits = new();

    // ── System Prompt ─────────────────────────────────────────────────
    private const string SystemPrompt = """
        Bạn là SAW Agri-Copilot — trợ lý AI chuyên gia nông nghiệp và đồng hành cùng Nhà phân phối nông sản trên hệ thống SAW (Smart Agri-Warehouse).
        
        ## VAI TRÒ & NHIỆM VỤ
        1. Giải đáp và tư vấn chuyên sâu về nông sản, tiêu chuẩn kiểm định chất lượng nông nghiệp (VietGAP, GlobalGAP, Organic, TCVN, HACCP...), các chỉ số kiểm nghiệm QC (Brix, độ ẩm, hoạt độ nước, dư lượng BVTV, kim loại nặng, vi sinh), kỹ thuật bảo quản và vận chuyển nông sản.
        2. Tra cứu dữ liệu thực tế của các lô hàng nông sản trên hệ thống SAW theo Mã lô (BatchCode).
        3. Hướng dẫn nghiệp vụ và quy trình nội bộ của hệ thống SAW (quy trình mua lô hàng, phân hạng chất lượng A/B/C/D/E của kho SAW, trạng thái lô hàng, quy tắc kho duyệt/từ chối lô, mã QR truy xuất).

        ## QUY TẮC TRA CỨU LÔ HÀNG
        - Nhà phân phối nhận diện và gọi lô hàng bằng Mã lô hàng (BatchCode, ví dụ: DEMO-DIST-LOT-01, PB-20261026-001...), KHÔNG PHẢI ID số của Database.
        - Khi người dùng hỏi về bất kỳ lô hàng nào: Dùng Mã lô hàng (BatchCode) để gọi tool GetBatchSummary hoặc GetBatchQcReport. TUYỆT ĐỐI KHÔNG yêu cầu người dùng nhập ID số của database.

        ## PHÂN ĐỊNH NGUỒN THÔNG TIN & NGUYÊN TẮC TRẢ LỜI

        ### 1. DỮ LIỆU LÔ HÀNG THỰC TẾ (Từ Tool: GetBatchSummary, GetBatchQcReport)
        - Khi người dùng hỏi về một lô hàng cụ thể theo Mã lô (BatchCode):
          + BẮT BUỘC gọi Tool GetBatchSummary hoặc GetBatchQcReport bằng Mã lô.
          + Trình bày CHÍNH XÁC những gì tool trả về (chỉ số QC, ngày thu hoạch, xuất xứ, hạng).
          + Không bịa đặt số liệu hay trạng thái của lô. Nếu thông tin chưa có trong hệ thống: nói rõ "Hệ thống chưa ghi nhận thông tin này cho lô hàng [Mã lô]".

        ### 2. KHO TRI THỨC HỆ THỐNG (Từ Tool: SearchKnowledgeBase)
        - Áp dụng cho:
          + Quy trình, quy định, chính sách, phân hạng A-E của kho SAW.
          + Mọi câu hỏi về nông sản, loại cây, kỹ thuật, cẩm nang, tài liệu được nạp vào hệ thống (PDF, Word, Markdown).
        - **Quy tắc cốt lõi:**
          + BẠN NÊN LUÔN GỌI Tool SearchKnowledgeBase để kiểm tra xem kho tài liệu hệ thống có nội dung liên quan hay không.
          + NẾU TÀI LIỆU TRONG KNOWLEDGE BASE CÓ THÔNG TIN: Bạn BẮT BUỘC PHẢI ƯU TIÊN SỐ 1 trả lời chính xác và trung thành theo nội dung tài liệu (ví dụ: nếu tài liệu ghi nhận thông tin hoặc quan điểm riêng như "trái cây ngon nhất là chanh vàng", quy trình mua hàng gồm các bước nào... thì PHẢI trả lời đúng theo tài liệu).
          + TUYỆT ĐỐI KHÔNG lấy kiến thức mặc định bên ngoài để bác bỏ hay làm sai lệch nội dung đã được ghi trong tài liệu hệ thống.

        ### 3. KIẾN THỨC NÔNG NGHIỆP BỔ TRỢ (Khi Knowledge Base không có thông tin)
        - Khi đã gọi SearchKnowledgeBase mà hệ thống báo không có tài liệu liên quan:
          + BẠN ĐƯỢC PHÉP VÀ ĐƯỢC KHUYẾN KHÍCH sử dụng kiến thức chuyên môn nông nghiệp sâu rộng của mình để giải thích rõ ràng, chi tiết, khoa học và dễ hiểu cho Nhà phân phối (về VietGAP, GlobalGAP, độ Brix, bảo quản...).
          + TUYỆT ĐỐI KHÔNG từ chối trả lời hoặc trả lời cộc lốc "hệ thống không có tài liệu". Hãy dùng chuyên môn nông nghiệp để hỗ trợ tốt nhất cho người dùng.

        ## PHẠM VI HỖ TRỢ
        - Chuyên sâu: Nông sản, lô hàng, kiểm định chất lượng, tiêu chuẩn an toàn (VietGAP, GlobalGAP...), bảo quản, vận chuyển và quy trình kho SAW.
        - Từ chối lịch sự: Chủ đề hoàn toàn ngoài lề nông nghiệp/kho vận (lập trình, chính trị, y tế con người, trò chơi...).

        ## PHONG CÁCH
        - Giao tiếp bằng Tiếng Việt, thân thiện, chuyên nghiệp, ngôn từ chuẩn mực ngành nông nghiệp.
        - Trình bày rõ ràng, dùng bullet points hoặc bảng ngắn khi cần so sánh/liệt kê.
        - Đi thẳng vào trọng tâm, giải thích thuật ngữ chuyên môn một cách dễ hiểu cho thương lái và nhà phân phối.
        - Highlight cảnh báo quan trọng bằng ⚠️
        """;


    public async Task<AiChatResponse> SendMessageAsync(int accountId, AiChatRequest request, CancellationToken ct)
    {
        // ── 1. Rate limiting ────────────────────────────────────────────
        if (!CheckRateLimit(accountId))
            return new AiChatResponse(
                $"Bạn đã gửi quá {options.RateLimitPerAccount} tin nhắn trong {options.RateLimitWindowMinutes} phút. Vui lòng thử lại sau.",
                IsError: true);

        // ── 2. Validate input ───────────────────────────────────────────
        var message = request.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(message) || message.Length > 2000)
            return new AiChatResponse("Tin nhắn không hợp lệ (tối đa 2000 ký tự).", IsError: true);

        // ── 3. Build conversation history (cap at MaxMessagesPerSession) ─
        var history = (request.History ?? [])
            .TakeLast(options.MaxMessagesPerSession * 2) // user + model pairs
            .Select(m => (m.Role, m.Content))
            .ToList();

        // ── 4. Inject batch context if provided ─────────────────────────
        var finalMessage = message;
        var batchCode = !string.IsNullOrWhiteSpace(request.BatchContextCode)
            ? request.BatchContextCode.Trim()
            : !string.IsNullOrWhiteSpace(request.BatchCode)
                ? request.BatchCode.Trim()
                : null;

        if (batchCode != null)
        {
            finalMessage = $"[Nhà phân phối đang xem lô hàng có Mã lô (BatchCode): \"{batchCode}\"]\n{message}";
        }
        else if (request.BatchContextId.HasValue)
        {
            finalMessage = $"[Nhà phân phối đang xem lô hàng ID: {request.BatchContextId}]\n{message}";
        }

        // ── 5. Call Gemini with function calling loop ───────────────────
        var reply = await geminiClient.ChatAsync(
            SystemPrompt,
            history,
            finalMessage,
            async (toolName, args) => await toolExecutor.ExecuteAsync(toolName, args, accountId, ct),
            ct);

        return new AiChatResponse(reply);
    }

    // ── Rate limiting (sliding window, in-memory) ──────────────────────
    private bool CheckRateLimit(int accountId)
    {
        var now = DateTime.UtcNow;
        var windowMs = TimeSpan.FromMinutes(options.RateLimitWindowMinutes);

        var (count, windowStart) = _rateLimits.GetOrAdd(accountId, _ => (0, now));

        if (now - windowStart > windowMs)
        {
            // Reset window
            _rateLimits[accountId] = (1, now);
            return true;
        }

        if (count >= options.RateLimitPerAccount)
            return false;

        _rateLimits[accountId] = (count + 1, windowStart);
        return true;
    }
}
