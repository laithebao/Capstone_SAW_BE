using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;
using static SAW.Application.Features.Suppliers.DTOs.DeclareSupplierProfileRequest;

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
            throw new KeyNotFoundException("Supplier profile not found. Please declare supplier information.");
        }

        // Đảm bảo chỉ chính chủ được xem profile
        if (profile.AccountId != currentAccountId)
        {
            throw new UnauthorizedAccessException("You are not allowed to view this supplier profile.");
        }

        return profile;
    }

    public async Task<SupplierProfileResponse> DeclareProfileAsync(int currentAccountId, DeclareSupplierProfileRequest request, CancellationToken cancellationToken = default)
    {
        var hasProfile = await _supplierRepository.HasProfileAsync(currentAccountId, cancellationToken);
        if (hasProfile)
        {
            throw new InvalidOperationException("Supplier profile already exists.");
        }

        var isTaxCodeExists = await _supplierRepository.IsTaxCodeExistsAsync(request.TaxCode, cancellationToken);
        if (isTaxCodeExists)
        {
            throw new ArgumentException("This tax code is already registered.");
        }

        var fullAddress = $"{request.Address.Trim()}, {request.Ward}, {request.District}, {request.Province}".Trim(',', ' ');
        var growingAreaInfo = $"{request.Province} - {request.District} (Diện tích: {request.FarmingAreaHa ?? 0} ha)";
        var newSupplierCode = await _supplierRepository.GenerateSupplierCodeAsync(cancellationToken);

        var newSupplier = new Supplier
        {
            AccountId = currentAccountId,
            SupplierCode = newSupplierCode,
            SupplierName = request.SupplierName.Trim(),
            TaxCode = request.TaxCode.Trim(),
            Address = fullAddress,
            ContactPerson = $"{request.ContactPerson.Trim()} | Đại diện PL: {request.LegalRepresentative.Trim()}",
            PhoneNumber = request.PhoneNumber?.Trim(),
            Email = request.Email?.Trim(),
            GrowingArea = growingAreaInfo,
            ProfileStatus = "ACTIVE",
            Note = request.SupplierType,
            CreatedAt = DateTime.UtcNow
        };

        await _supplierRepository.AddSupplierAsync(newSupplier, request.CropTypeIds, request.Certifications, cancellationToken);

        var createdProfile = await _supplierRepository.GetProfileByAccountIdAsync(currentAccountId, cancellationToken);
        return createdProfile ?? throw new Exception("Failed to load supplier profile after creation.");
    }

    public async Task<SupplierProfileResponse> UpdateProfileAsync(int currentAccountId, UpdateSupplierProfileRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra Supplier Profile có tồn tại và thuộc sở hữu không
        var supplier = await _supplierRepository.GetEntityByAccountIdAsync(currentAccountId, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Supplier profile not found.");
        }

        // 2. Kiểm tra trùng Mã số thuế với bản ghi khác
        var isTaxCodeExists = await _supplierRepository.IsTaxCodeExistsExceptCurrentAsync(request.TaxCode, supplier.SupplierId, cancellationToken);
        if (isTaxCodeExists)
        {
            throw new ArgumentException("This tax code is already registered.");
        }

        // 3. Backup dữ liệu cũ cho Audit Log
        var oldValuesJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            supplier.SupplierName,
            supplier.TaxCode,
            supplier.Address,
            supplier.GrowingArea,
            supplier.ContactPerson,
            supplier.PhoneNumber,
            supplier.Email
        });

        // 4. Cập nhật thông tin mới (Giữ nguyên ProfileStatus)
        var fullAddress = $"{request.Address.Trim()}, {request.Ward}, {request.District}, {request.Province}".Trim(',', ' ');
        var growingAreaInfo = $"{request.Province} - {request.District} (Diện tích: {request.FarmingAreaHa ?? 0} ha)";

        supplier.SupplierName = request.SupplierName.Trim();
        supplier.TaxCode = request.TaxCode.Trim();
        supplier.Address = fullAddress;
        supplier.ContactPerson = $"{request.ContactPerson.Trim()} | Đại diện PL: {request.LegalRepresentative.Trim()}";
        supplier.PhoneNumber = request.PhoneNumber?.Trim();
        supplier.Email = request.Email?.Trim();
        supplier.GrowingArea = growingAreaInfo;
        supplier.Note = request.SupplierType;

        // 5. Lưu xuống DB
        await _supplierRepository.UpdateSupplierAsync(supplier, request.CropTypeIds, request.Certifications, oldValuesJson, cancellationToken);

        // 6. Trả về Supplier Profile sau khi cập nhật
        var updatedProfile = await _supplierRepository.GetProfileByAccountIdAsync(currentAccountId, cancellationToken);
        return updatedProfile ?? throw new Exception("Failed to load supplier profile after update.");
    }
}
