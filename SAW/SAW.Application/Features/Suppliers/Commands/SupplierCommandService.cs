using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;
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
            PhoneNumber = request.PhoneNumber?.Trim(),
            Email = request.Email?.Trim(),
            ProfileStatus = "ACTIVE",
            Note = request.SupplierType,
            CreatedAt = DateTime.UtcNow
            
            // ĐÃ XÓA: GrowingArea = BuildGrowingAreaInfo(...)
        };

        var normalizedCertifications = request.GetNormalizedCertifications();

        // LƯU Ý: Bạn cần truyền thêm danh sách ID vùng trồng (GrowingAreaIds) vào hàm AddSupplierAsync
        // để Repository thêm dữ liệu vào bảng trung gian SupplierGrowingArea.
        await _supplierRepository.AddSupplierAsync(
            newSupplier,
            request.CropTypeIds ?? new List<int>(),
            normalizedCertifications,
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

        var isTaxCodeExists = await _supplierRepository.IsTaxCodeExistsExceptCurrentAsync(request.TaxCode, supplier.SupplierId, cancellationToken);
        if (isTaxCodeExists)
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
        supplier.TaxCode = request.TaxCode.Trim();
        supplier.Address = request.Address?.Trim() ?? string.Empty;
        supplier.ContactPerson = BuildContactPerson(request.ContactPerson, request.LegalRepresentative);
        supplier.PhoneNumber = request.PhoneNumber?.Trim();
        supplier.Email = request.Email?.Trim();
        supplier.Note = request.SupplierType;

        var normalizedCertifications = request.GetNormalizedCertifications();

        // LƯU Ý: Việc cập nhật vùng trồng vào bảng trung gian nên thực hiện trong Repository
        await _supplierRepository.UpdateSupplierAsync(
            supplier,
            request.CropTypeIds ?? new List<int>(),
            normalizedCertifications,
            oldValuesJson,
            cancellationToken
        );

        return await GetMyProfileAsync(currentAccountId, cancellationToken);
    }

    #region Helper Methods
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