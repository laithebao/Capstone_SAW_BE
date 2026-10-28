namespace SAW.Application.Features.AiChat;

/// <summary>
/// Đại diện một tin nhắn trong lịch sử hội thoại AI.
/// </summary>
/// <param name="Role">Vai trò: "user" | "model"</param>
/// <param name="Content">Nội dung tin nhắn.</param>
public sealed record ChatMessage(string Role, string Content);
