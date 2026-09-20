using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;
using System.Text.Json;
using System.Text.RegularExpressions;

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

        // Bổ sung: Tự động trích xuất FarmingAreaHa từ OperatingRegion (GrowingArea) nếu đang null hoặc 0
        if ((!profile.FarmingAreaHa.HasValue || profile.FarmingAreaHa == 0) && !string.IsNullOrEmpty(profile.OperatingRegion))
        {
            profile.FarmingAreaHa = ExtractFarmingArea(profile.OperatingRegion);
        }

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

        var fullAddress = BuildFullAddress(request.Address, request.Ward, request.District, request.Province);
        var growingAreaInfo = BuildGrowingAreaInfo(request.Province, request.District, request.FarmingAreaHa);
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
            GrowingArea = growingAreaInfo,
            ProfileStatus = "ACTIVE",
            Note = request.SupplierType,
            CreatedAt = DateTime.UtcNow
        };

        var normalizedCertifications = request.GetNormalizedCertifications();

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
        // 1. Kiểm tra Supplier Profile tồn tại
        var supplier = await _supplierRepository.GetEntityByAccountIdAsync(currentAccountId, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Không tìm thấy thông tin nhà cung cấp.");
        }

        // 2. Kiểm tra trùng Mã số thuế
        var isTaxCodeExists = await _supplierRepository.IsTaxCodeExistsExceptCurrentAsync(request.TaxCode, supplier.SupplierId, cancellationToken);
        if (isTaxCodeExists)
        {
            throw new ArgumentException("Mã số thuế này đã được đăng ký.");
        }

        // 3. Backup dữ liệu cũ cho Audit Log
        var oldValuesJson = JsonSerializer.Serialize(new
        {
            supplier.SupplierName,
            supplier.TaxCode,
            supplier.Address,
            supplier.GrowingArea,
            supplier.ContactPerson,
            supplier.PhoneNumber,
            supplier.Email
        });

        // 4. Nếu request gửi FarmingAreaHa = null/0, bảo toàn số cũ bằng cách trích xuất lại từ DB (GrowingArea)
        var effectiveFarmingArea = request.FarmingAreaHa;
        if ((!effectiveFarmingArea.HasValue || effectiveFarmingArea == 0) && !string.IsNullOrEmpty(supplier.GrowingArea))
        {
            var oldExtractedArea = ExtractFarmingArea(supplier.GrowingArea);
            if (oldExtractedArea.HasValue && oldExtractedArea.Value > 0)
            {
                effectiveFarmingArea = oldExtractedArea;
            }
        }

        // 5. Cập nhật thông tin mới
        supplier.SupplierName = request.SupplierName.Trim();
        supplier.TaxCode = request.TaxCode.Trim();
        supplier.Address = BuildFullAddress(request.Address, request.Ward, request.District, request.Province);
        supplier.ContactPerson = BuildContactPerson(request.ContactPerson, request.LegalRepresentative);
        supplier.PhoneNumber = request.PhoneNumber?.Trim();
        supplier.Email = request.Email?.Trim();
        supplier.GrowingArea = BuildGrowingAreaInfo(request.Province, request.District, effectiveFarmingArea);
        supplier.Note = request.SupplierType;

        var normalizedCertifications = request.GetNormalizedCertifications();

        // 6. Lưu xuống DB
        await _supplierRepository.UpdateSupplierAsync(
            supplier,
            request.CropTypeIds ?? new List<int>(),
            normalizedCertifications,
            oldValuesJson,
            cancellationToken
        );

        // 7. Trả về thông tin đã cập nhật (GetMyProfileAsync sẽ tự đảm bảo FarmingAreaHa luôn có giá trị)
        return await GetMyProfileAsync(currentAccountId, cancellationToken);
    }

    #region Helper Methods
    private static string BuildFullAddress(string? address, string? ward, string? district, string? province)
    {
        var parts = new[] { address?.Trim(), ward?.Trim(), district?.Trim(), province?.Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(", ", parts);
    }

    private static string BuildGrowingAreaInfo(string? province, string? district, decimal? area)
    {
        var location = string.Join(" - ", new[] { province?.Trim(), district?.Trim() }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrEmpty(location)) location = "Chưa xác định";
        return $"{location} (Diện tích: {area ?? 0} ha)";
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

    /// <summary>
    /// Trích xuất số diện tích (ha) từ chuỗi GrowingArea / OperatingRegion (ví dụ từ: "... (Diện tích: 15.5 ha)")
    /// </summary>
    private static decimal? ExtractFarmingArea(string? growingArea)
    {
        if (string.IsNullOrWhiteSpace(growingArea)) return null;

        var match = Regex.Match(growingArea, @"\(Diện tích:\s*([\d\.,]+)\s*ha\)", RegexOptions.IgnoreCase);
        if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var area))
        {
            return area;
        }

        return null;
    }
    #endregion
}