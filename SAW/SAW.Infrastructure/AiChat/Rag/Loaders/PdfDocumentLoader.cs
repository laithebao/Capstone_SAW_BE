using System.Text;
using UglyToad.PdfPig;

namespace SAW.Infrastructure.AiChat.Rag.Loaders;

/// <summary>
/// Loader cho file PDF (.pdf) — sử dụng thư viện UglyToad.PdfPig.
/// Trích xuất văn bản từng trang và ghép lại.
/// </summary>
public sealed class PdfDocumentLoader : IDocumentLoader
{
    public IReadOnlyList<string> SupportedExtensions => [".pdf"];

    public Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        using var document = PdfDocument.Open(filePath);
        foreach (var page in document.GetPages())
        {
            var pageText = page.Text;
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                sb.AppendLine(pageText.Trim());
                sb.AppendLine(); // khoảng cách giữa các trang
            }
        }

        return Task.FromResult(sb.ToString().Trim());
    }
}
