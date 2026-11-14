using System.Text.RegularExpressions;

namespace SAW.Domain.Common;

public static class ContactValidation
{
    public static string? NormalizePhone(string? value) => string.IsNullOrWhiteSpace(value)
        ? null : Regex.Replace(value.Trim(), @"[\s.-]", string.Empty);

    public static bool IsVietnamPhone(string? value) => string.IsNullOrWhiteSpace(value)
        || Regex.IsMatch(NormalizePhone(value)!, @"\A(?:0[0-9]{9}|\+84[0-9]{9})\z", RegexOptions.CultureInvariant);

    // Same address format as Zod email in the current registration form.
    public static bool IsEmail(string? value) => string.IsNullOrWhiteSpace(value)
        || Regex.IsMatch(value.Trim(), @"\A(?!\.)(?!.*\.\.)[A-Za-z0-9_'+.\-]*[A-Za-z0-9_+\-]@(?:[A-Za-z0-9][A-Za-z0-9\-]*\.)+[A-Za-z]{2,}\z", RegexOptions.CultureInvariant);
}
