using SAW.Application.Exceptions;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using System.Text.Json;

namespace SAW.Application.Features.InspectionStandards;

public sealed class InspectionStandardService(IInspectionStandardRepository repository) : IInspectionStandardService
{
    // ── List ──────────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<InspectionStandardListItem>> ListAsync(string? search, CancellationToken token)
        => repository.ListAsync(search?.Trim(), token);

    // ── Get Detail ────────────────────────────────────────────────────────────
    public async Task<InspectionStandardDetailDto> GetByIdAsync(int id, CancellationToken token)
    {
        var set = await repository.GetByIdAsync(id, token)
            ?? throw new NotFoundException($"Không tìm thấy bộ tiêu chuẩn kiểm định với ID {id}.");

        var versions = set.Versions
            .OrderByDescending(v => v.VersionNo)
            .Select(v => new InspectionStandardVersionDto(
                v.InspectionStandardVersionId,
                v.VersionNo,
                v.VersionStatus,
                v.EffectiveFrom,
                v.EffectiveTo,
                v.CreatedAt,
                v.Criteria.Count,
                v.Criteria.Select(c => MapCriterion(c)).ToList()))
            .ToList();

        return new InspectionStandardDetailDto(
            set.InspectionStandardSetId,
            set.StandardCode,
            set.StandardName,
            set.Description,
            set.CropTypeId,
            set.CropType.CropName,
            set.IsActive,
            set.CreatedAt,
            versions);
    }

    // ── UC12 – Create Standard Set ────────────────────────────────────────────
    public async Task<InspectionStandardDto> CreateAsync(CreateInspectionStandardRequest request, CancellationToken token)
    {
        // Validation
        if (request.CropTypeId <= 0 || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            throw new BadRequestException("Loại nông sản, mã và tên bộ tiêu chuẩn là bắt buộc.");
        if (request.VersionNo < 1)
            throw new BadRequestException("Số phiên bản phải lớn hơn hoặc bằng 1.");
        if (request.Criteria == null || request.Criteria.Count == 0)
            throw new BadRequestException("Cần có ít nhất một tiêu chí kiểm định.");
        if (!await repository.CropTypeExistsAsync(request.CropTypeId, token))
            throw new BadRequestException("Loại nông sản không hợp lệ hoặc đã ngừng hoạt động.");

        var code = request.Code.Trim().ToUpperInvariant();
        if (await repository.CodeExistsAsync(code, token))
            throw new ConflictException("Mã bộ tiêu chuẩn đã tồn tại.");

        ValidateCriteria(request.Criteria);

        // Build entities
        var standard = new InspectionStandardSet
        {
            CropTypeId   = request.CropTypeId,
            StandardCode = code,
            StandardName = request.Name.Trim(),
            Description  = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IsActive     = true,
            CreatedAt    = DateTime.UtcNow
        };

        var version = new InspectionStandardVersion
        {
            VersionNo     = request.VersionNo,
            VersionStatus = "PUBLISHED",
            EffectiveFrom = request.EffectiveFrom,
            CreatedAt     = DateTime.UtcNow
        };

        foreach (var item in request.Criteria)
            version.Criteria.Add(BuildCriterion(item));

        standard.Versions.Add(version);
        repository.Add(standard);
        await repository.SaveChangesAsync(token);

        // Explicit summary audit log for UC12
        repository.AddAuditLog(new AuditLog
        {
            ActionType  = "CREATE_INSPECTION_STANDARD",
            EntityName  = "INSPECTION_STANDARD_SET",
            EntityId    = standard.InspectionStandardSetId.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                SetCode     = standard.StandardCode,
                SetName     = standard.StandardName,
                CropTypeId  = standard.CropTypeId,
                VersionNo   = version.VersionNo,
                Status      = version.VersionStatus,
                CriteriaCount = version.Criteria.Count
            }),
            Description = $"Tạo bộ tiêu chuẩn '{standard.StandardCode} – {standard.StandardName}' phiên bản v{version.VersionNo} với {version.Criteria.Count} tiêu chí.",
            CreatedAt   = DateTime.UtcNow
        });
        await repository.SaveChangesAsync(token);

        return new InspectionStandardDto(
            standard.InspectionStandardSetId,
            standard.StandardCode,
            standard.StandardName,
            standard.CropTypeId,
            version.VersionNo,
            version.Criteria.Count);
    }

    // ── UC13 – Create New Version ─────────────────────────────────────────────
    public async Task<InspectionStandardVersionCreatedDto> CreateVersionAsync(
        int setId, CreateInspectionStandardVersionRequest request, CancellationToken token)
    {
        if (request.Criteria == null || request.Criteria.Count == 0)
            throw new BadRequestException("Cần có ít nhất một tiêu chí kiểm định.");

        var set = await repository.GetByIdAsync(setId, token)
            ?? throw new NotFoundException($"Không tìm thấy bộ tiêu chuẩn kiểm định với ID {setId}.");

        if (!set.IsActive)
            throw new BadRequestException("Không thể tạo phiên bản mới cho bộ tiêu chuẩn đã ngừng hoạt động.");

        ValidateCriteria(request.Criteria);

        // Auto-increment version number
        var maxVersionNo = await repository.GetMaxVersionNoAsync(setId, token);
        var newVersionNo = maxVersionNo + 1;

        // Retire older versions
        foreach (var oldVersion in set.Versions)
        {
            if (oldVersion.VersionStatus != "RETIRED")
            {
                oldVersion.VersionStatus = "RETIRED";
            }
        }

        var version = new InspectionStandardVersion
        {
            InspectionStandardSetId = setId,
            VersionNo               = newVersionNo,
            VersionStatus           = "PUBLISHED",
            EffectiveFrom           = request.EffectiveFrom,
            CreatedAt               = DateTime.UtcNow
        };

        foreach (var item in request.Criteria)
            version.Criteria.Add(BuildCriterion(item));

        set.Versions.Add(version);
        await repository.SaveChangesAsync(token);

        // Explicit summary audit log for UC13
        repository.AddAuditLog(new AuditLog
        {
            ActionType  = "CREATE_INSPECTION_STANDARD_VERSION",
            EntityName  = "INSPECTION_STANDARD_VERSION",
            EntityId    = version.InspectionStandardVersionId.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                SetCode       = set.StandardCode,
                SetName       = set.StandardName,
                VersionNo     = version.VersionNo,
                Status        = version.VersionStatus,
                EffectiveFrom = version.EffectiveFrom,
                CriteriaCount = version.Criteria.Count
            }),
            Description = $"Tạo phiên bản mới v{version.VersionNo} cho bộ tiêu chuẩn '{set.StandardCode} – {set.StandardName}' với {version.Criteria.Count} tiêu chí.",
            CreatedAt   = DateTime.UtcNow
        });
        await repository.SaveChangesAsync(token);

        return new InspectionStandardVersionCreatedDto(
            set.InspectionStandardSetId,
            set.StandardCode,
            set.StandardName,
            version.InspectionStandardVersionId,
            version.VersionNo,
            version.VersionStatus,
            version.EffectiveFrom,
            version.Criteria.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    // Allowed values – must match DB CHECK constraints exactly
    private static readonly HashSet<string> AllowedGroups    = new(StringComparer.OrdinalIgnoreCase) { "ENVIRONMENT", "LAB", "SENSORY" };
    private static readonly HashSet<string> AllowedDataTypes = new(StringComparer.OrdinalIgnoreCase) { "NUMBER", "TEXT", "BOOLEAN" };

    private static void ValidateCriteria(IReadOnlyList<SaveInspectionCriterionRequest> criteria)
    {
        if (criteria.Any(x => string.IsNullOrWhiteSpace(x.Code) || string.IsNullOrWhiteSpace(x.Name)))
            throw new BadRequestException("Mỗi tiêu chí cần có mã và tên.");

        if (criteria.GroupBy(x => x.Code.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new BadRequestException("Mã tiêu chí không được trùng lặp trong cùng một phiên bản.");

        if (criteria.Any(x => x.MinValue.HasValue && x.MaxValue.HasValue && x.MinValue > x.MaxValue))
            throw new BadRequestException("Giá trị tối thiểu không được lớn hơn giá trị tối đa.");

        // Validate DataType – bắt buộc, phải khớp DB CHECK constraint
        var missingType = criteria.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.DataType));
        if (missingType is not null)
            throw new BadRequestException($"Tiêu chí '{missingType.Code}' thiếu loại dữ liệu (DataType).");

        var badType = criteria.FirstOrDefault(x => !AllowedDataTypes.Contains(x.DataType.Trim()));
        if (badType is not null)
            throw new BadRequestException(
                $"Loại dữ liệu '{badType.DataType}' không hợp lệ. Cho phép: NUMBER, TEXT, BOOLEAN.");

        // Validate CriterionGroup – nếu có giá trị thì phải khớp DB CHECK constraint
        var badGroup = criteria.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CriterionGroup)
            && !AllowedGroups.Contains(x.CriterionGroup.Trim()));
        if (badGroup is not null)
            throw new BadRequestException(
                $"Nhóm tiêu chí '{badGroup.CriterionGroup}' không hợp lệ. Cho phép: ENVIRONMENT, LAB, SENSORY.");
    }

    private static InspectionCriterion BuildCriterion(SaveInspectionCriterionRequest item)
    {
        var criterion = new InspectionCriterion
        {
            CriterionCode  = item.Code.Trim().ToUpperInvariant(),
            CriterionName  = item.Name.Trim(),
            CriterionGroup = string.IsNullOrWhiteSpace(item.CriterionGroup) ? "ENVIRONMENT" : item.CriterionGroup.Trim().ToUpperInvariant(),
            DataType       = item.DataType.Trim().ToUpperInvariant(),
            Unit           = string.IsNullOrWhiteSpace(item.Unit) ? null : item.Unit.Trim(),
            IsRequired     = item.IsRequired,
            IsCritical     = item.IsCritical
        };

        // Only create a grade rule when the user provided threshold data
        bool hasThreshold = item.MinValue.HasValue || item.MaxValue.HasValue
            || !string.IsNullOrWhiteSpace(item.RequiredTextValue);

        if (hasThreshold)
        {
            criterion.GradeRules.Add(new CriterionGradeRule
            {
                Grade             = "A",   // Grade A = meets standard
                MinValue          = item.MinValue,
                MaxValue          = item.MaxValue,
                RequiredTextValue = string.IsNullOrWhiteSpace(item.RequiredTextValue) ? null : item.RequiredTextValue.Trim(),
                IsFailRule        = item.IsFailRule
            });
        }

        return criterion;
    }

    private static CriterionDto MapCriterion(InspectionCriterion c)
    {
        // Take the first grade rule (grade A = primary pass rule) if any
        var rule = c.GradeRules.OrderBy(r => r.Grade).FirstOrDefault();
        return new CriterionDto(
            c.InspectionCriterionId,
            c.CriterionCode,
            c.CriterionName,
            c.CriterionGroup,
            c.DataType,
            c.Unit,
            c.IsRequired,
            c.IsCritical,
            rule?.MinValue,
            rule?.MaxValue,
            rule?.RequiredTextValue,
            rule?.IsFailRule ?? false);
    }
}

