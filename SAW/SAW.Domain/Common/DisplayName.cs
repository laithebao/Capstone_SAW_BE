using System.Globalization;
using System.Text;

namespace SAW.Domain.Common;

public static class DisplayName
{
    public static string CapitalizeFirst(string value)
    {
        var text = value.Trim().Normalize(NormalizationForm.FormC);
        if (text.Length == 0) return text;
        var first = StringInfo.GetNextTextElement(text);
        return first.ToUpper(CultureInfo.GetCultureInfo("vi-VN")) + text[first.Length..];
    }
}
