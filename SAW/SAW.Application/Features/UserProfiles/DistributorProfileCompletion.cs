using SAW.Domain.Common;
using SAW.Domain.Entities;

namespace SAW.Application.Features.UserProfiles;

public static class DistributorProfileCompletion
{
    public static bool HasRealTaxCode(string? value) => Text(value, 50, true)
        && !value!.Trim().StartsWith("GOOGLE-", StringComparison.OrdinalIgnoreCase);

    // Optional fields stay optional, using the same validation as profile editing.
    public static bool IsComplete(Account account) => account.Distributor is { ProfileStatus: "ACTIVE" } d
        && Text(account.FullName, 150, true) && Text(d.DistributorName, 200, true) && HasRealTaxCode(d.TaxCode)
        && Text(d.ContactPerson, 150) && Text(d.Address, 500) && Text(d.Email, 255)
        && Text(account.PhoneNumber, 30) && Text(d.PhoneNumber, 30)
        && ContactValidation.IsVietnamPhone(account.PhoneNumber) && ContactValidation.IsVietnamPhone(d.PhoneNumber)
        && ContactValidation.IsEmail(d.Email);

    private static bool Text(string? value, int max, bool required = false) =>
        (!required || !string.IsNullOrWhiteSpace(value)) && (value?.Trim().Length ?? 0) <= max;
}
