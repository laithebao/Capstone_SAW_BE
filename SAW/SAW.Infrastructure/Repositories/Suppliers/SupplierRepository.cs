using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Repositories.Suppliers;

public class SupplierRepository : ISupplierRepository
{
    private readonly DbContext _context;

    public SupplierRepository(DbContext context)
    {
        _context = context;
    }

    public async Task<SupplierProfileResponse?> GetProfileByAccountIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await (from s in _context.Set<Supplier>()
                      join a in _context.Set<Account>() on s.AccountId equals a.AccountId
                      where s.AccountId == accountId
                      select new SupplierProfileResponse
                      {
                          SupplierId = s.SupplierId,
                          AccountId = s.AccountId,
                          SupplierCode = s.SupplierCode,
                          SupplierName = s.SupplierName,
                          TaxCode = s.TaxCode,
                          Address = s.Address,
                          OperatingRegion = s.GrowingArea,
                          ProfileStatus = s.ProfileStatus,
                          ContactPerson = s.ContactPerson,
                          PhoneNumber = s.PhoneNumber ?? a.PhoneNumber,
                          Email = s.Email ?? a.Email,
                          LegalRepresentative = s.ContactPerson,
                          LogoUrl = a.AvatarUrl,
                          CropTypes = (from sct in _context.Set<SupplierCropType>()
                                       join ct in _context.Set<CropType>() on sct.CropTypeId equals ct.CropTypeId
                                       where sct.SupplierId == s.SupplierId && sct.IsActive
                                       select new SupplierCropTypeDto
                                       {
                                           CropTypeId = ct.CropTypeId,
                                           CropCode = ct.CropCode,
                                           CropName = ct.CropName,
                                           CategoryName = ct.CategoryName
                                       }).ToList(),
                          Certifications = (from sc in _context.Set<SupplierCertification>()
                                            where sc.SupplierId == s.SupplierId && sc.IsActive
                                            select new SupplierCertificationDto
                                            {
                                                SupplierCertificationId = sc.SupplierCertificationId,
                                                CertificationName = sc.CertificationName,
                                                CertificateNumber = sc.CertificateNumber,
                                                IssuingOrganization = sc.IssuingOrganization,
                                                IssueDate = sc.IssueDate,
                                                ExpiryDate = sc.ExpiryDate,
                                                EvidenceFileUrl = sc.EvidenceFileUrl,
                                                IsActive = sc.IsActive
                                            }).ToList()
                      }).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> HasProfileAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<Supplier>()
                             .AnyAsync(s => s.AccountId == accountId, cancellationToken);
    }

    public async Task<bool> IsTaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default)
    {
        var cleanTaxCode = taxCode.Trim();
        var existsInSupplier = await _context.Set<Supplier>().AnyAsync(s => s.TaxCode == cleanTaxCode, cancellationToken);
        var existsInDistributor = await _context.Set<Distributor>().AnyAsync(d => d.TaxCode == cleanTaxCode, cancellationToken);

        return existsInSupplier || existsInDistributor;
    }

    public async Task<string> GenerateSupplierCodeAsync(CancellationToken cancellationToken = default)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"NCC-{year}-";

        var lastSupplier = await _context.Set<Supplier>()
            .Where(s => s.SupplierCode.StartsWith(prefix))
            .OrderByDescending(s => s.SupplierCode)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastSupplier == null)
        {
            return $"{prefix}001";
        }

        var lastSequenceStr = lastSupplier.SupplierCode.Replace(prefix, "");
        if (int.TryParse(lastSequenceStr, out int lastSeq))
        {
            return $"{prefix}{(lastSeq + 1):D3}";
        }

        return $"{prefix}{Guid.NewGuid().ToString()[..3].ToUpper()}";
    }

    public async Task AddSupplierAsync(Supplier supplier, List<int> cropTypeIds, List<string> certifications, CancellationToken cancellationToken = default)
    {
        using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // 1. Lưu Supplier Profile
            _context.Set<Supplier>().Add(supplier);
            await _context.SaveChangesAsync(cancellationToken);

            // 2. Lưu liên kết Nông sản (SupplierCropType)
            if (cropTypeIds.Any())
            {
                var supplierCropTypes = cropTypeIds.Select(ctId => new SupplierCropType
                {
                    SupplierId = supplier.SupplierId,
                    CropTypeId = ctId,
                    IsActive = true
                });
                _context.Set<SupplierCropType>().AddRange(supplierCropTypes);
            }

            // 3. Lưu Chứng nhận (SupplierCertification)
            if (certifications.Any())
            {
                var supplierCerts = certifications
                    .Where(c => c != "Không có chứng nhận")
                    .Select(certName => new SupplierCertification
                    {
                        SupplierId = supplier.SupplierId,
                        CertificationName = certName,
                        IsActive = true
                    });
                _context.Set<SupplierCertification>().AddRange(supplierCerts);
            }

            // 4. Ghi Audit Log chuẩn thuộc tính Entity AuditLog
            var auditLog = new AuditLog
            {
                AccountId = supplier.AccountId,
                ActionType = "DECLARE_SUPPLIER_PROFILE",
                EntityName = "SUPPLIER",
                EntityId = supplier.SupplierId.ToString(),
                NewDataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    supplier.SupplierCode,
                    supplier.SupplierName,
                    supplier.TaxCode,
                    supplier.ProfileStatus
                }),
                Description = "Khai báo thông tin Nhà cung cấp mới.",
                CreatedAt = DateTime.UtcNow
            };
            _context.Set<AuditLog>().Add(auditLog);

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> IsTaxCodeExistsExceptCurrentAsync(string taxCode, int currentSupplierId, CancellationToken cancellationToken = default)
    {
        var cleanTaxCode = taxCode.Trim();
        var existsInSupplier = await _context.Set<Supplier>()
            .AnyAsync(s => s.TaxCode == cleanTaxCode && s.SupplierId != currentSupplierId, cancellationToken);

        var existsInDistributor = await _context.Set<Distributor>()
            .AnyAsync(d => d.TaxCode == cleanTaxCode, cancellationToken);

        return existsInSupplier || existsInDistributor;
    }

    public async Task UpdateSupplierAsync(Supplier supplier, List<int> cropTypeIds, List<string> certifications, string oldValuesJson, CancellationToken cancellationToken = default)
    {
        using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // 1. Cập nhật Supplier Profile (Giữ nguyên ProfileStatus cũ)
            _context.Set<Supplier>().Update(supplier);

            // 2. Cập nhật lại danh mục Nông sản (SupplierCropType)
            var oldCropTypes = await _context.Set<SupplierCropType>()
                .Where(sct => sct.SupplierId == supplier.SupplierId)
                .ToListAsync(cancellationToken);
            _context.Set<SupplierCropType>().RemoveRange(oldCropTypes);

            if (cropTypeIds.Any())
            {
                var newCropTypes = cropTypeIds.Select(ctId => new SupplierCropType
                {
                    SupplierId = supplier.SupplierId,
                    CropTypeId = ctId,
                    IsActive = true
                });
                _context.Set<SupplierCropType>().AddRange(newCropTypes);
            }

            // 3. Cập nhật danh sách Chứng nhận (SupplierCertification)
            var oldCerts = await _context.Set<SupplierCertification>()
                .Where(sc => sc.SupplierId == supplier.SupplierId)
                .ToListAsync(cancellationToken);
            _context.Set<SupplierCertification>().RemoveRange(oldCerts);

            if (certifications.Any())
            {
                var newCerts = certifications
                    .Where(c => c != "Không có chứng nhận")
                    .Select(certName => new SupplierCertification
                    {
                        SupplierId = supplier.SupplierId,
                        CertificationName = certName,
                        IsActive = true
                    });
                _context.Set<SupplierCertification>().AddRange(newCerts);
            }

            // 4. Ghi Audit Log cho hành động EDIT_SUPPLIER_PROFILE
            var auditLog = new AuditLog
            {
                AccountId = supplier.AccountId,
                ActionType = "EDIT_SUPPLIER_PROFILE",
                EntityName = "SUPPLIER",
                EntityId = supplier.SupplierId.ToString(),
                OldDataJson = oldValuesJson,
                NewDataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    supplier.SupplierCode,
                    supplier.SupplierName,
                    supplier.TaxCode,
                    supplier.Address,
                    supplier.GrowingArea,
                    supplier.ProfileStatus
                }),
                Description = "Cập nhật thông tin Hồ sơ Nhà cung cấp.",
                CreatedAt = DateTime.UtcNow
            };
            _context.Set<AuditLog>().Add(auditLog);

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<Supplier?> GetEntityByAccountIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<Supplier>().FirstOrDefaultAsync(s => s.AccountId == accountId, cancellationToken);
    }
}