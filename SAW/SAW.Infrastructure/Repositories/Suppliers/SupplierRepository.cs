using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories.Suppliers;

public class SupplierRepository : ISupplierRepository
{
    private readonly AppDbContext _context;

    private async Task<List<SupplierGrowingAreaInputDto>> ResolveAreasAsync(int supplierId, List<SupplierGrowingAreaInputDto> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0) throw new ArgumentException("Vui lòng nhập ít nhất một vùng trồng.");
        var result = new List<SupplierGrowingAreaInputDto>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (input.AreaInHectares.HasValue && (!double.IsFinite(input.AreaInHectares.Value) || input.AreaInHectares <= 0)) throw new ArgumentException("Diện tích phải là số dương.");
            var area = input.GrowingAreaId > 0 ? await _context.GrowingAreas.SingleOrDefaultAsync(x => x.GrowingAreaId == input.GrowingAreaId && (x.OwnerSupplierId == null || x.OwnerSupplierId == supplierId), ct) : null;
            if (input.GrowingAreaId > 0 && area is null) throw new ArgumentException("Vùng trồng không tồn tại hoặc không thuộc quyền sử dụng.");
            var name = (input.AreaName ?? area?.AreaName)?.Trim().Normalize();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) || !names.Add(name)) throw new ArgumentException("Tên vùng trồng không hợp lệ hoặc bị trùng.");
            if (area is null || area.AreaName != name && (area.OwnerSupplierId != supplierId || await _context.ProductBatches.AnyAsync(x => x.GrowingAreaId == area.GrowingAreaId, ct)))
            {
                area = new GrowingArea { AreaName = name, OwnerSupplierId = supplierId };
                _context.GrowingAreas.Add(area); await _context.SaveChangesAsync(ct);
            }
            else area.AreaName = name;
            result.Add(new SupplierGrowingAreaInputDto { GrowingAreaId = area.GrowingAreaId, AreaName = name, AreaInHectares = input.AreaInHectares });
        }
        return result;
    }

    private async Task ValidateCropSelections(int supplierId, List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new ArgumentException("Chọn ít nhất một nông sản, không được trùng.");
        var retained = await _context.SupplierCropTypes.Where(x => x.SupplierId == supplierId).Select(x => x.CropTypeId).ToListAsync(ct);
        var valid = await _context.CropTypes.Where(x => ids.Contains(x.CropTypeId) && (retained.Contains(x.CropTypeId) || x.IsActive && x.Category != null && x.Category.IsActive && x.Category.Parent != null && x.Category.Parent.IsActive)).CountAsync(ct);
        if (valid != ids.Count) throw new ArgumentException("Nông sản hoặc danh mục đã ngừng hoạt động hoặc không tồn tại.");
    }

    public SupplierRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<SupplierProfileResponse?> GetProfileByAccountIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        var profile = await (from s in _context.Set<Supplier>()
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
                          GrowingAreas = (from sga in _context.Set<SupplierGrowingArea>()
                                          join ga in _context.Set<GrowingArea>() on sga.GrowingAreaId equals ga.GrowingAreaId
                                          where sga.SupplierId == s.SupplierId
                                          select new SupplierGrowingAreaDto
                                          {
                                              GrowingAreaId = ga.GrowingAreaId,
                                              AreaName = ga.AreaName,
                                              Province = ga.Province,
                                              District = ga.District,
                                              Ward = ga.Ward,
                                              AreaInHectares = sga.AreaInHectares
                                          }).ToList(),
                          ProfileStatus = s.ProfileStatus,
                          ContactPerson = s.ContactPerson,
                          PhoneNumber = s.PhoneNumber ?? a.PhoneNumber,
                          Email = s.Email ?? a.Email,
                          LegalRepresentative = s.LegalRepresentative ?? "",
                          SupplierType = s.Note,
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
        if (profile != null)
        {
            profile.Documents = await SupplierFileLinks.Documents(_context, profile.SupplierId, null, cancellationToken);
            profile.Documents.AddRange(profile.Certifications.Where(c => !string.IsNullOrWhiteSpace(c.EvidenceFileUrl))
                .Select(c => new SupplierDocumentDto { FileName = c.CertificationName, FileUrl = c.EvidenceFileUrl! }));
        }
        return profile;

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
        var year = SAW.Domain.Common.VietnamTime.Today.Year;
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

    public async Task AddSupplierAsync(Supplier supplier, List<int> cropTypeIds, List<SupplierCertificationInputDto> certifications, List<SupplierGrowingAreaInputDto> growingAreas, string? logoUrl, List<string>? documentUrls, CancellationToken cancellationToken = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                _context.Set<Supplier>().Add(supplier);
                await _context.SaveChangesAsync(cancellationToken);

                // 1. VALIDATE & THÊM VÙNG TRỒNG (ĐÃ CỦNG CỐ AN TOÀN)
                growingAreas = await ResolveAreasAsync(supplier.SupplierId, growingAreas, cancellationToken);
                await ValidateCropSelections(supplier.SupplierId, cropTypeIds, cancellationToken);
                if (growingAreas != null && growingAreas.Any())
                {
                    var inputAreaIds = growingAreas.Select(ga => ga.GrowingAreaId).Distinct().ToList();
                    var existingAreaIds = await _context.Set<GrowingArea>()
                        .Where(ga => inputAreaIds.Contains(ga.GrowingAreaId))
                        .Select(ga => ga.GrowingAreaId)
                        .ToListAsync(cancellationToken);

                    var invalidIds = inputAreaIds.Except(existingAreaIds).ToList();
                    if (invalidIds.Any())
                    {
                        throw new ArgumentException($"Các Vùng trồng có ID [{string.Join(", ", invalidIds)}] không tồn tại trong cơ sở dữ liệu.");
                    }

                    var supplierGrowingAreas = growingAreas.Select(ga => new SupplierGrowingArea
                    {
                        SupplierId = supplier.SupplierId,
                        GrowingAreaId = ga.GrowingAreaId,
                        AreaInHectares = ga.AreaInHectares,
                        JoinedAt = DateTime.UtcNow
                    });
                    _context.Set<SupplierGrowingArea>().AddRange(supplierGrowingAreas);
                }

                if (cropTypeIds != null && cropTypeIds.Any())
                {
                    var supplierCropTypes = cropTypeIds.Select(ctId => new SupplierCropType
                    {
                        SupplierId = supplier.SupplierId,
                        CropTypeId = ctId,
                        IsActive = true
                    });
                    _context.Set<SupplierCropType>().AddRange(supplierCropTypes);
                }

                await SupplierFileLinks.ReplaceDocuments(_context, supplier.AccountId, supplier.SupplierId, null, documentUrls, cancellationToken);
                await SupplierFileLinks.SaveAvatar(_context, supplier.AccountId, supplier.SupplierId, logoUrl, cancellationToken);
                if (certifications != null && certifications.Any())
                {
                    var supplierCerts = certifications
                        .Where(c => !string.IsNullOrWhiteSpace(c.CertificationName) && c.CertificationName != "Không có chứng nhận")
                        .Select(cert => new SupplierCertification
                        {
                            SupplierId = supplier.SupplierId,
                            CertificationName = cert.CertificationName,
                            EvidenceFileUrl = cert.EvidenceFileUrl,
                            IsActive = true
                        });
                    _context.Set<SupplierCertification>().AddRange(supplierCerts);
                }

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
        });
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

    public async Task UpdateSupplierAsync(Supplier supplier, List<int> cropTypeIds, List<SupplierCertificationInputDto> certifications, List<SupplierGrowingAreaInputDto> growingAreas, string oldValuesJson, string? logoUrl, List<string>? documentUrls, CancellationToken cancellationToken = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                _context.Set<Supplier>().Update(supplier);
                growingAreas = await ResolveAreasAsync(supplier.SupplierId, growingAreas, cancellationToken);
                await ValidateCropSelections(supplier.SupplierId, cropTypeIds, cancellationToken);

                // 1. Xóa các liên kết vùng trồng cũ của Supplier
                var oldGrowingAreas = await _context.Set<SupplierGrowingArea>()
                    .Where(sga => sga.SupplierId == supplier.SupplierId)
                    .ToListAsync(cancellationToken);

                var areaIds = growingAreas.Select(a => a.GrowingAreaId).Distinct().ToList();
                var foundAreas = await _context.GrowingAreas.Where(a => areaIds.Contains(a.GrowingAreaId)).CountAsync(cancellationToken);
                if (foundAreas != areaIds.Count || areaIds.Count != growingAreas.Count)
                    throw new ArgumentException("Vùng trồng không hợp lệ hoặc bị chọn trùng.");
                _context.SupplierGrowingAreas.RemoveRange(oldGrowingAreas.Where(a => !areaIds.Contains(a.GrowingAreaId)));
                foreach (var input in growingAreas)
                {
                    var link = oldGrowingAreas.SingleOrDefault(a => a.GrowingAreaId == input.GrowingAreaId);
                    if (link == null)
                    {
                        link = new SupplierGrowingArea { SupplierId = supplier.SupplierId, GrowingAreaId = input.GrowingAreaId, JoinedAt = DateTime.UtcNow };
                        _context.SupplierGrowingAreas.Add(link);
                    }
                    link.AreaInHectares = input.AreaInHectares;
                }
                var oldCrops = await _context.SupplierCropTypes.Where(c => c.SupplierId == supplier.SupplierId).ToListAsync(cancellationToken);
                var cropIds = cropTypeIds.Distinct().ToList();
                _context.SupplierCropTypes.RemoveRange(oldCrops.Where(c => !cropIds.Contains(c.CropTypeId)));
                foreach (var cropId in cropIds)
                {
                    var link = oldCrops.SingleOrDefault(c => c.CropTypeId == cropId);
                    if (link != null) link.IsActive = true;
                    else _context.SupplierCropTypes.Add(new SupplierCropType { SupplierId = supplier.SupplierId, CropTypeId = cropId, IsActive = true });
                }

                await SupplierFileLinks.ReplaceDocuments(_context, supplier.AccountId, supplier.SupplierId, null, documentUrls, cancellationToken);
                await SupplierFileLinks.SaveAvatar(_context, supplier.AccountId, supplier.SupplierId, logoUrl, cancellationToken);
                supplier.UpdatedAt = DateTime.UtcNow;
                var oldCerts = await _context.Set<SupplierCertification>()
                    .Where(sc => sc.SupplierId == supplier.SupplierId)
                    .ToListAsync(cancellationToken);
                _context.Set<SupplierCertification>().RemoveRange(oldCerts);

                if (certifications != null && certifications.Any())
                {
                    var newCerts = certifications
                        .Where(c => !string.IsNullOrWhiteSpace(c.CertificationName) && c.CertificationName != "Không có chứng nhận")
                        .Select(cert => new SupplierCertification
                        {
                            SupplierId = supplier.SupplierId,
                            CertificationName = cert.CertificationName,
                            EvidenceFileUrl = cert.EvidenceFileUrl,
                            IsActive = true
                        });
                    _context.Set<SupplierCertification>().AddRange(newCerts);
                }

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
        });
    }

    public async Task<Supplier?> GetEntityByAccountIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<Supplier>().Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.AccountId == accountId, cancellationToken);
    }
}
