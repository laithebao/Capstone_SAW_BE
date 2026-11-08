using System.Text.RegularExpressions;

namespace SAW.Infrastructure.AiChat.Rag;

/// <summary>
/// Chia văn bản dài thành các chunk chồng nhau (overlapping chunks) phù hợp để embedding.
/// Ưu tiên cắt tại ranh giới tự nhiên: đoạn văn → câu → từ.
/// </summary>
public static class DocumentChunker
{
    /// <summary>
    /// Chia văn bản thành các chunk chồng nhau.
    /// </summary>
    /// <param name="text">Văn bản đầu vào.</param>
    /// <param name="chunkSize">Kích thước tối đa mỗi chunk (ký tự). Mặc định 1000.</param>
    /// <param name="overlap">Số ký tự chồng nhau giữa các chunk liên tiếp. Mặc định 150.</param>
    public static IEnumerable<string> Chunk(string text, int chunkSize = 1000, int overlap = 150)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        text = NormalizeWhitespace(text);

        if (text.Length <= chunkSize)
        {
            yield return text;
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + chunkSize, text.Length);

            // Tìm điểm cắt tự nhiên nếu chưa đến cuối
            if (end < text.Length)
            {
                int naturalBreak = FindNaturalBreak(text, start, end);
                if (naturalBreak > start + overlap)
                    end = naturalBreak;
            }

            var chunk = text[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
                yield return chunk;

            if (end >= text.Length) break;
            start = end - overlap;
            if (start < 0) start = 0;
        }
    }

    private static int FindNaturalBreak(string text, int start, int preferredEnd)
    {
        int searchWindow = Math.Min(250, preferredEnd - start);

        // 1. Ưu tiên: ranh giới đoạn văn (\n\n)
        int idx = text.LastIndexOf("\n\n", preferredEnd, searchWindow);
        if (idx > start) return idx + 2;

        // 2. Ranh giới dòng
        idx = text.LastIndexOf('\n', preferredEnd, Math.Min(searchWindow, 150));
        if (idx > start) return idx + 1;

        // 3. Ranh giới câu (.!?)
        for (int i = preferredEnd; i > preferredEnd - 150 && i > start; i--)
        {
            if (i < text.Length && (text[i] == '.' || text[i] == '!' || text[i] == '?'))
                return i + 1;
        }

        // 4. Ranh giới từ
        idx = text.LastIndexOf(' ', preferredEnd, Math.Min(50, preferredEnd - start));
        if (idx > start) return idx + 1;

        return preferredEnd;
    }

    private static string NormalizeWhitespace(string text)
    {
        // Chuẩn hóa line endings, bỏ spaces/tabs thừa
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{4,}", "\n\n\n"); // giữ tối đa 3 newline liên tiếp
        return text.Trim();
    }
}
