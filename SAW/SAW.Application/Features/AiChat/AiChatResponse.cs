namespace SAW.Application.Features.AiChat;

/// <summary>
/// Kết quả phản hồi từ AI chatbot.
/// </summary>
/// <param name="Reply">Nội dung phản hồi của AI.</param>
/// <param name="IsError">True nếu có lỗi xảy ra trong quá trình xử lý.</param>
public sealed record AiChatResponse(string Reply, bool IsError = false);
