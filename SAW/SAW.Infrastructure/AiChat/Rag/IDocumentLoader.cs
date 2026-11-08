namespace SAW.Infrastructure.AiChat.Rag;

/// <summary>
/// Contract cho các loader trích xuất văn bản từ các định dạng file khác nhau.
/// </summary>
public interface IDocumentLoader
{
    /// <summary>Danh sách extension được hỗ trợ (chữ thường, có dấu chấm, ví dụ: ".pdf").</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Trích xuất toàn bộ văn bản từ file.</summary>
    Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default);
}
