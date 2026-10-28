using System.Text;
using System.Text.RegularExpressions;

namespace SAW.Infrastructure.AiChat.Rag.Loaders;

/// <summary>
/// Loader cho file Markdown (.md, .markdown).
/// Xóa cú pháp Markdown để lấy văn bản thuần túy, giúp embedding chính xác hơn.
/// </summary>
public sealed class MarkdownTextLoader : IDocumentLoader
{
    public IReadOnlyList<string> SupportedExtensions => [".md", ".markdown"];

    public Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        var raw = File.ReadAllText(filePath, Encoding.UTF8);
        var cleaned = StripMarkdown(raw);
        return Task.FromResult(cleaned);
    }

    private static string StripMarkdown(string md)
    {
        // Xóa header markers (## Title → Title)
        md = Regex.Replace(md, @"^#{1,6}\s+", string.Empty, RegexOptions.Multiline);
        // Bold/Italic: **text** / *text* / ***text*** → text
        md = Regex.Replace(md, @"\*{1,3}([^*\n]+)\*{1,3}", "$1");
        md = Regex.Replace(md, @"_{1,3}([^_\n]+)_{1,3}", "$1");
        // Code inline: `code` → code
        md = Regex.Replace(md, @"`([^`]+)`", "$1");
        // Code blocks
        md = Regex.Replace(md, @"```[\s\S]*?```", string.Empty);
        // Links: [text](url) → text
        md = Regex.Replace(md, @"\[([^\]]+)\]\([^\)]*\)", "$1");
        // Table separator rows (|---|---|)
        md = Regex.Replace(md, @"^\|[\s\-:|]+\|$", string.Empty, RegexOptions.Multiline);
        // Table cell pipes
        md = Regex.Replace(md, @"\|", " | ");
        // HTML tags
        md = Regex.Replace(md, @"<[^>]+>", string.Empty);
        // Normalize blank lines
        md = Regex.Replace(md, @"\n{3,}", "\n\n");

        return md.Trim();
    }
}
