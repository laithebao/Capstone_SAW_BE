using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SAW.Infrastructure.AiChat.Rag.Loaders;

/// <summary>
/// Loader cho file Word (.docx) — sử dụng thư viện DocumentFormat.OpenXml.
/// Trích xuất văn bản từ toàn bộ các đoạn văn (Paragraph) trong tài liệu.
/// </summary>
public sealed class WordDocumentLoader : IDocumentLoader
{
    public IReadOnlyList<string> SupportedExtensions => [".docx"];

    public Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
        var body = wordDoc.MainDocumentPart?.Document?.Body;
        if (body is null) return Task.FromResult(string.Empty);

        foreach (var para in body.Elements<Paragraph>())
        {
            // Gom text từ tất cả các Run trong đoạn văn
            var paraText = string.Concat(
                para.Elements<Run>().Select(r => r.InnerText));

            if (!string.IsNullOrWhiteSpace(paraText))
                sb.AppendLine(paraText.Trim());
        }

        return Task.FromResult(sb.ToString().Trim());
    }
}
