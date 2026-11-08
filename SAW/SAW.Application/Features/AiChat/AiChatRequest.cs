namespace SAW.Application.Features.AiChat;

/// <summary>
/// Yêu cầu gửi tin nhắn tới AI chatbot.
/// </summary>
/// <param name="Message">Nội dung tin nhắn người dùng.</param>
/// <param name="BatchContextId">ID số của lô hàng đang xem (nếu có).</param>
/// <param name="BatchContextCode">Mã lô hàng (BatchCode) đang xem — dùng để AI tra cứu lô hàng.</param>
/// <param name="BatchCode">Alias cho BatchContextCode (tương thích ngược).</param>
/// <param name="History">Lịch sử hội thoại trước đó.</param>
public sealed record AiChatRequest(
    string Message,
    long? BatchContextId = null,
    string? BatchContextCode = null,
    string? BatchCode = null,
    IReadOnlyList<ChatMessage>? History = null);
