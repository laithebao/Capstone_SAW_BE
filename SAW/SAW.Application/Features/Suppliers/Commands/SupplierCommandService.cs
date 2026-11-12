using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;
using SAW.Domain.Common;
using System.Text.Json;

namespace SAW.Application.Features.Suppliers.Commands;

public class SupplierCommandService : ISupplierCommandService
{
    private readonly ISupplierRepository _supplierRepository;

    public SupplierCommandService(ISupplierRepository supplierRepository)
    {
        _supplierRepository = supplierRepository;
    }

    public async Task<SupplierProfileResponse> GetMyProfileAsync(int currentAccountId, CancellationToken cancellationToken = default)
    {
        var profile = await _supplierRepository.GetProfileByAccountIdAsync(currentAccountId, cancellationToken);

        if (profile == null)
        {
            throw new KeyNotFoundException("Không tìm thấy thông tin nhà cung cấp. Vui lòng khai báo thông tin nhà cung cấp.");
        }

        if (profile.AccountId != currentAccountId)
        {
            throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin nhà cung cấp này.");
        }

        // ĐÃ XÓA: Các đoạn code dùng Regex cắt chuỗi "(Diện tích: ... ha)" 
        // Dữ liệu FarmingAreaHa và OperatingRegion bây giờ nên được query thẳng từ
        // bảng SupplierGrowingArea thông qua hàm GetProfileByAccountIdAsync của Repository.

        return profile;
    }

    public async Task<SupplierProfileResponse> DeclareProfileAsync(int currentAccountId, DeclareSupplierProfileRequest request, CancellationToken cancellationToken = default)
    {
        var hasProfile = await _supplierRepository.HasProfileAsync(currentAccountId, cancellationToken);
        if (hasProfile)
        {
            throw new InvalidOperationException("Hồ sơ nhà cung cấp đã tồn tại.");
        }

        ValidateContact(request);

        var isTaxCodeExists = await _supplierRepository.IsTaxCodeExistsAsync(request.TaxCode, cancellationToken);
        if (isTaxCodeExists)
        {
            throw new ArgumentException("Mã số thuế này đã được đăng ký.");
        }

        var fullAddress = request.Address?.Trim() ?? string.Empty;
        var newSupplierCode = await _supplierRepository.GenerateSupplierCodeAsync(cancellationToken);

        var newSupplier = new Supplier
        {
            AccountId = currentAccountId,
            SupplierCode = newSupplierCode,
            SupplierName = request.SupplierName.Trim(),
            TaxCode = request.TaxCode.Trim(),
            Address = fullAddress,
            ContactPerson = BuildContactPerson(request.ContactPerson, request.LegalRepresentative),
            PhoneNumber = ContactValidation.NormalizePhone(request.PhoneNumber),
            Email = request.Email?.Trim(),
            ProfileStatus = "ACTIVE",
            Note = request.SupplierType,
            CreatedAt = DateTime.UtcNow
            
            // ĐÃ XÓA: GrowingArea = BuildGrowingAreaInfo(...)
        };

        var normalizedCertifications = request.GetNormalizedCertifications();

        await _supplierRepository.AddSupplierAsync(
            newSupplier,
            request.CropTypeIds ?? new List<int>(),
            normalizedCertifications,
            request.GrowingAreas ?? new List<SupplierGrowingAreaInputDto>(),
            request.LogoUrl, request.EvidenceDocumentUrls,
            cancellationToken
        );

        return await GetMyProfileAsync(currentAccountId, cancellationToken);
    }

    public async Task<SupplierProfileResponse> UpdateProfileAsync(int currentAccountId, UpdateSupplierProfileRequest request, CancellationToken cancellationToken = default)
    {
        var supplier = await _supplierRepository.GetEntityByAccountIdAsync(currentAccountId, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Không tìm thấy thông tin nhà cung cấp.");
        }

        var canCompleteTaxCode = supplier.ProfileStatus == "PENDING_REVIEW"
            && supplier.TaxCode.StartsWith("GOOGLE-", StringComparison.Ordinal)
            && supplier.Account?.Username.StartsWith("google_", StringComparison.Ordinal) == true;
        var taxCode = request.TaxCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(taxCode)) throw new ArgumentException("Mã số thuế không được để trống.");
        if (!canCompleteTaxCode && !string.Equals(taxCode, supplier.TaxCode, StringComparison.Ordinal))
            throw new ArgumentException("Mã số thuế đã đăng ký không thể thay đổi trong hồ sơ.");
        ValidateContact(request);
        if (canCompleteTaxCode && await _supplierRepository.IsTaxCodeExistsExceptCurrentAsync(taxCode, supplier.SupplierId, cancellationToken))
        {
            throw new ArgumentException("Mã số thuế này đã được đăng ký.");
        }

        // ĐÃ XÓA: supplier.GrowingArea khỏi đối tượng Json backup
        var oldValuesJson = JsonSerializer.Serialize(new
        {
            supplier.SupplierName,
            supplier.TaxCode,
            supplier.Address,
            supplier.ContactPerson,
            supplier.PhoneNumber,
            supplier.Email
        });

        supplier.SupplierName = request.SupplierName.Trim();
        if (canCompleteTaxCode) supplier.TaxCode = taxCode;
        supplier.Address = request.Address?.Trim() ?? string.Empty;
        supplier.ContactPerson = BuildContactPerson(request.ContactPerson, request.LegalRepresentative);
        supplier.PhoneNumber = ContactValidation.NormalizePhone(request.PhoneNumber);
        supplier.Email = request.Email?.Trim();
        supplier.Note = request.SupplierType;

        // Google registration creates a placeholder profile. Completing it follows
        // the same ACTIVE outcome as DeclareProfileAsync after email verification.
        if (supplier.ProfileStatus == "PENDING_REVIEW"
            && supplier.Account is { AccountStatus: "ACTIVE" } account
            && account.Username.StartsWith("google_", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(supplier.PhoneNumber))
            supplier.ProfileStatus = "ACTIVE";

        var normalizedCertifications = request.GetNormalizedCertifications();

        await _supplierRepository.UpdateSupplierAsync(
            supplier,
            request.CropTypeIds ?? new List<int>(),
            normalizedCertifications,
            request.GrowingAreas ?? new List<SupplierGrowingAreaInputDto>(),
            oldValuesJson, request.LogoUrl, request.EvidenceDocumentUrls,
            cancellationToken
        );

        return await GetMyProfileAsync(currentAccountId, cancellationToken);
    }

    #region Helper Methods
    private static void ValidateContact(DeclareSupplierProfileRequest request)
    {
        if (request.PhoneNumber?.Trim().Length > 20 || !ContactValidation.IsVietnamPhone(request.PhoneNumber))
            throw new ArgumentException("Số điện thoại Việt Nam không hợp lệ. Nhập 10 chữ số bắt đầu bằng 0 hoặc +84 và 9 chữ số.");
        if (request.Email?.Trim().Length > 150 || !ContactValidation.IsEmail(request.Email))
            throw new ArgumentException("Email liên hệ không đúng định dạng hoặc vượt quá 150 ký tự.");
    }

    private static string BuildFullAddress(string? address, string? ward, string? district, string? province)
    {
        var parts = new[] { address?.Trim(), ward?.Trim(), district?.Trim(), province?.Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(", ", parts);
    }

    private static string BuildContactPerson(string? contactPerson, string? legalRepresentative)
    {
        var rawContact = contactPerson?.Trim() ?? "";

        if (rawContact.Contains("| Đại diện PL:"))
        {
            rawContact = rawContact.Split("| Đại diện PL:")[0].Trim();
        }

        var lr = legalRepresentative?.Trim();

        if (string.IsNullOrEmpty(lr) || string.Equals(rawContact, lr, StringComparison.OrdinalIgnoreCase))
        {
            return rawContact;
        }

        return $"{rawContact} | Đại diện PL: {lr}";
    }
    
    // ĐÃ XÓA: BuildGrowingAreaInfo và ExtractFarmingArea vì logic regex bóc tách đã vô dụng
    // khi dữ liệu nằm ở bảng chuẩn hóa.
    #endregion
}
