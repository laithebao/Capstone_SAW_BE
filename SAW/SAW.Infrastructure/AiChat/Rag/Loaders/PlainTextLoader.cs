using System.Text;

namespace SAW.Infrastructure.AiChat.Rag.Loaders;

/// <summary>
/// Loader cho file văn bản thuần (.txt).
/// </summary>
public sealed class PlainTextLoader : IDocumentLoader
{
    public IReadOnlyList<string> SupportedExtensions => [".txt"];

    public Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
        => Task.FromResult(File.ReadAllText(filePath, Encoding.UTF8).Trim());
}
