using SAW.Application.Exceptions;
using SAW.Domain.Common;
using SAW.Domain.Entities;

namespace SAW.Application.Features.UserProfiles;

public sealed class UserProfileService(IUserProfileRepository repository) : IUserProfileService
{
    public async Task<UserProfileResponse> GetAsync(int accountId, CancellationToken cancellationToken) =>
        Map(await FindAsync(accountId, cancellationToken));

    public async Task<UserProfileResponse> UpdateAsync(int accountId, UpdateUserProfileRequest request, CancellationToken cancellationToken)
    {
        var account = await FindAsync(accountId, cancellationToken);
        var errors = new List<string>();
        RequiredText(request.FullName, 150, "Họ và tên", errors);
        Phone(request.PhoneNumber, "Số điện thoại cá nhân", errors);
        var business = request.Distributor;
        if (account.Role.RoleCode == "DISTRIBUTOR")
        {
            if (account.Distributor is null) throw new NotFoundException("Không tìm thấy hồ sơ nhà phân phối.");
            if (business is null) errors.Add("Vui lòng điền thông tin nhà phân phối.");
            else
            {
                RequiredText(business.DistributorName, 200, "Tên nhà phân phối", errors);
                if (business.TaxCode is not null && !string.Equals(business.TaxCode.Trim(), account.Distributor.TaxCode, StringComparison.Ordinal))
                    errors.Add("Mã số thuế không thể thay đổi trong hồ sơ.");
                OptionalText(business.ContactPerson, 150, "Người liên hệ", errors);
                OptionalText(business.Address, 500, "Địa chỉ", errors);
                Phone(business.PhoneNumber, "Số điện thoại liên hệ", errors);
                OptionalText(business.Email, 255, "Email liên hệ", errors);
                if (!ContactValidation.IsEmail(business.Email))
                    errors.Add("Email liên hệ không hợp lệ.");
            }
        }
        else if (business is not null) errors.Add("Vai trò hiện tại không có hồ sơ nhà phân phối.");

        if (errors.Count > 0) throw new ValidationException(errors);
        // Validate the complete request before changing either tracked entity.
        account.FullName = request.FullName.Trim();
        account.PhoneNumber = ContactValidation.NormalizePhone(request.PhoneNumber);
        account.UpdatedAt = DateTime.UtcNow;
        if (business is not null)
        {
            var distributor = account.Distributor!;
            distributor.DistributorName = business.DistributorName.Trim();
            distributor.ContactPerson = Normalize(business.ContactPerson);
            distributor.PhoneNumber = ContactValidation.NormalizePhone(business.PhoneNumber);
            distributor.Email = Normalize(business.Email);
            distributor.Address = Normalize(business.Address);
            distributor.UpdatedAt = account.UpdatedAt;
        }
        await repository.SaveChangesAsync(cancellationToken);
        return Map(account);
    }

    private async Task<Account> FindAsync(int accountId, CancellationToken cancellationToken)
    {
        var account = await repository.GetByAccountIdAsync(accountId, cancellationToken)
            ?? throw new NotFoundException("Không tìm thấy hồ sơ người dùng.");
        if (account.Role.RoleCode is not ("ADMINISTRATOR" or "WAREHOUSE_MANAGER" or "QC_STAFF" or "OPERATION_STAFF" or "DISTRIBUTOR"))
            throw new ForbiddenException("Vui lòng sử dụng trang hồ sơ dành cho vai trò của bạn.");
        return account;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void RequiredText(string? value, int maxLength, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{label} không được để trống.");
        else OptionalText(value, maxLength, label, errors);
    }
    private static void OptionalText(string? value, int maxLength, string label, List<string> errors)
    {
        if (value?.Trim().Length > maxLength) errors.Add($"{label} không được vượt quá {maxLength} ký tự.");
    }
    private static void Phone(string? value, string label, List<string> errors)
    {
        OptionalText(value, 30, label, errors);
        if (!ContactValidation.IsVietnamPhone(value))
            errors.Add($"{label} không hợp lệ. Vui lòng nhập 10 chữ số bắt đầu bằng 0 hoặc +84 và 9 chữ số.");
    }
    private static UserProfileResponse Map(Account account) => new(
        account.AccountId, account.Username, account.Email, account.FullName, account.PhoneNumber,
        account.AvatarUrl, account.Role.RoleCode,
        account.Role.RoleCode == "DISTRIBUTOR" && account.Distributor is { } distributor
            ? new(distributor.DistributorId, distributor.DistributorCode, distributor.DistributorName,
                distributor.TaxCode, distributor.ContactPerson, distributor.PhoneNumber, distributor.Email, distributor.Address)
            : null);
}
