using SAW.Application.Exceptions;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using System.Text.Json;

namespace SAW.Application.Features.InspectionStandards;

public sealed class InspectionStandardService(IInspectionStandardRepository repository) : IInspectionStandardService
{
    // ── Allowed values – must match DB CHECK constraints exactly ─────────────
    private static readonly HashSet<string> AllowedGroups    = new(StringComparer.OrdinalIgnoreCase) { "ENVIRONMENT", "LAB", "SENSORY" };
    private static readonly HashSet<string> AllowedDataTypes = new(StringComparer.OrdinalIgnoreCase) { "NUMBER", "TEXT", "BOOLEAN" };
    private static readonly HashSet<string> AllowedGrades    = new(StringComparer.OrdinalIgnoreCase) { "A", "B", "C", "D", "E" };

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
                v.Criteria.Select(MapCriterion).ToList()))
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
        // Validation – header
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

        // Audit log
        repository.AddAuditLog(new AuditLog
        {
            ActionType  = "CREATE_INSPECTION_STANDARD",
            EntityName  = "INSPECTION_STANDARD_SET",
            EntityId    = standard.InspectionStandardSetId.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                SetCode       = standard.StandardCode,
                SetName       = standard.StandardName,
                CropTypeId    = standard.CropTypeId,
                VersionNo     = version.VersionNo,
                Status        = version.VersionStatus,
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
                oldVersion.VersionStatus = "RETIRED";
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

        // Audit log
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

    /// <summary>
    /// Validates all criteria in a version request.
    /// Rules:
    ///  1. Mỗi tiêu chí phải có mã và tên.
    ///  2. Mã tiêu chí không trùng trong cùng phiên bản.
    ///  3. DataType phải là NUMBER | TEXT | BOOLEAN.
    ///  4. CriterionGroup phải là SENSORY | LAB | ENVIRONMENT.
    ///  5. Mỗi tiêu chí phải có ít nhất 1 grade rule (BOOLEAN miễn).
    ///  6. Grade trong rule phải thuộc A–E.
    ///  7. Không có 2 rules cùng grade trên cùng 1 tiêu chí.
    ///  8. NUMBER rule: phải có MinValue hoặc MaxValue; Min ≤ Max nếu cả hai có.
    ///  9. TEXT rule: phải có RequiredTextValue.
    /// 10. IsCritical = true → phải có ít nhất 1 hạng IsFailRule = true (ngưỡng từ chối). [Bỏ qua cho BOOLEAN]
    /// 10a. IsFailRule = true trên bất kỳ hạng nào → tiêu chí bắt buộc phải IsCritical = true. [Bỏ qua cho BOOLEAN]
    ///      Vì chỉ tiêu chí Nghiêm trọng mới có quyền từ chối lô hàng.
    /// 10b. BOOLEAN bắt buộc IsCritical = true (Tiêu chuẩn Nhà nước/Quốc tế: False = Từ chối lô).
    /// 11. Hạng A không bao giờ là ngưỡng từ chối (vô nghĩa nghiệp vụ).
    /// 12. Các grade phải liên tục từ A xuống (A, AB, ABC, ABCD, ABCDE) — không được bỏ hạng giữa.
    /// 13. NUMBER: khoảng [Min, Max] của các grades không được chồng lấp nhau.
    /// 14. CASCADE: Nếu hạng X là ngưỡng từ chối, mọi hạng tệ hơn X (vị trí sau trong A→E) cũng phải là ngưỡng từ chối.
    /// </summary>
    private static void ValidateCriteria(IReadOnlyList<SaveInspectionCriterionRequest> criteria)
    {
        // Rule 1: Mã và tên bắt buộc
        if (criteria.Any(x => string.IsNullOrWhiteSpace(x.Code) || string.IsNullOrWhiteSpace(x.Name)))
            throw new BadRequestException("Mỗi tiêu chí cần có mã và tên.");

        // Rule 2: Mã không trùng
        if (criteria.GroupBy(x => x.Code.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new BadRequestException("Mã tiêu chí không được trùng lặp trong cùng một phiên bản.");

        // Rule 3: DataType hợp lệ
        var missingType = criteria.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.DataType));
        if (missingType is not null)
            throw new BadRequestException($"Tiêu chí '{missingType.Code}' thiếu loại dữ liệu (DataType).");

        var badType = criteria.FirstOrDefault(x => !AllowedDataTypes.Contains(x.DataType.Trim()));
        if (badType is not null)
            throw new BadRequestException($"Loại dữ liệu '{badType.DataType}' không hợp lệ. Cho phép: NUMBER, TEXT, BOOLEAN.");

        // Rule 4: CriterionGroup hợp lệ
        var badGroup = criteria.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CriterionGroup)
            && !AllowedGroups.Contains(x.CriterionGroup.Trim()));
        if (badGroup is not null)
            throw new BadRequestException($"Nhóm tiêu chí '{badGroup.CriterionGroup}' không hợp lệ. Cho phép: ENVIRONMENT, LAB, SENSORY.");

        foreach (var criterion in criteria)
        {
            var code = criterion.Code.Trim().ToUpperInvariant();
            var dataType = criterion.DataType.Trim().ToUpperInvariant();
            var rules = criterion.GradeRules;

            // Rule 5: BOOLEAN không bắt buộc grade rules (Pass/Fail tự động)
            // NUMBER và TEXT phải có ít nhất 1 grade rule
            if (dataType != "BOOLEAN" && (rules == null || rules.Count == 0))
                throw new BadRequestException($"Tiêu chí '{code}' (loại {dataType}) phải có ít nhất 1 quy tắc phân hạng (grade rule).");

            if (rules == null || rules.Count == 0) continue;

            // Rule 6: Grade phải thuộc A–E
            var badGrade = rules.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Grade) || !AllowedGrades.Contains(r.Grade.Trim()));
            if (badGrade is not null)
                throw new BadRequestException($"Tiêu chí '{code}': grade '{badGrade.Grade}' không hợp lệ. Cho phép: A, B, C, D, E.");

            // Rule 7: Không trùng grade trong cùng tiêu chí
            if (rules.GroupBy(r => r.Grade.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new BadRequestException($"Tiêu chí '{code}': không được có 2 quy tắc cùng hạng (grade).");

            // Rule 8: NUMBER – phải có Min hoặc Max, và Min ≤ Max
            if (dataType == "NUMBER")
            {
                var missingRange = rules.FirstOrDefault(r => !r.MinValue.HasValue && !r.MaxValue.HasValue);
                if (missingRange is not null)
                    throw new BadRequestException($"Tiêu chí '{code}' hạng {missingRange.Grade}: phải có MinValue hoặc MaxValue.");

                var invalidRange = rules.FirstOrDefault(r => r.MinValue.HasValue && r.MaxValue.HasValue && r.MinValue > r.MaxValue);
                if (invalidRange is not null)
                    throw new BadRequestException($"Tiêu chí '{code}' hạng {invalidRange.Grade}: MinValue không được lớn hơn MaxValue.");
            }

            // Rule 9: TEXT – phải có RequiredTextValue cho mỗi rule
            if (dataType == "TEXT")
            {
                var missingText = rules.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.RequiredTextValue));
                if (missingText is not null)
                    throw new BadRequestException($"Tiêu chí '{code}' hạng {missingText.Grade}: phải nhập giá trị chấp nhận được (RequiredTextValue) cho tiêu chí kiểu TEXT.");
            }

            // Rule 10b: BOOLEAN bắt buộc IsCritical = true
            // Theo Tiêu chuẩn Nhà nước/Quốc tế: tiêu chí Boolean chỉ xuất hiện khi có giới hạn an toàn tuyệt đối
            // VD: phát hiện Aflatoxin, thuốc trừ sâu cấm, ký sinh trùng... — False = Từ chối lô ngay
            if (dataType == "BOOLEAN" && !criterion.IsCritical)
                throw new BadRequestException(
                    $"Tiêu chí '{code}' (kiểu BOOLEAN) bắt buộc phải được đánh dấu 'Nghiêm trọng'. " +
                    "Tiêu chí kiểm tra Đạt/Không đạt theo Tiêu chuẩn Quốc gia luôn mang tính chất quyết định: " +
                    "kết quả Không đạt sẽ tự động Từ chối toàn bộ lô hàng.");

            // Rule 10: Tiêu chí Nghiêm trọng phải có ít nhất 1 hạng là ngưỡng từ chối [Bỏ qua BOOLEAN]
            // BOOLEAN Nghiêm trọng không có Grade Rules — engine đánh giá trực tiếp giá trị BooleanValue
            if (criterion.IsCritical && dataType != "BOOLEAN" && !rules.Any(r => r.IsFailRule))
                throw new BadRequestException(
                    $"Tiêu chí '{code}' được đánh dấu 'Nghiêm trọng' nhưng chưa xác định hạng nào là ngưỡng từ chối. " +
                    "Cần chỉ định ít nhất 1 hạng (thường D hoặc E) làm ngưỡng từ chối lô hàng.");

            // Rule 10a: Ngưỡng từ chối chỉ có nghĩa khi tiêu chí là Nghiêm trọng [Bỏ qua BOOLEAN]
            if (!criterion.IsCritical && dataType != "BOOLEAN" && rules.Any(r => r.IsFailRule))
                throw new BadRequestException(
                    $"Tiêu chí '{code}': không thể đặt ngưỡng từ chối khi tiêu chí chưa được đánh dấu 'Nghiêm trọng'. " +
                    "Chỉ tiêu chí Nghiêm trọng mới có quyền từ chối toàn bộ lô hàng khi vi phạm ngưỡng.");

            // Rule 11: Hạng A không bao giờ là ngưỡng từ chối — hạng tốt nhất không thể từ chối
            var gradeAFail = rules.FirstOrDefault(r => r.Grade.Trim().Equals("A", StringComparison.OrdinalIgnoreCase) && r.IsFailRule);
            if (gradeAFail is not null)
                throw new BadRequestException(
                    $"Tiêu chí '{code}': Hạng A (chất lượng tốt nhất) không thể là ngưỡng từ chối. " +
                    "Chỉ hạng D hoặc E mới hợp lệ làm ngưỡng từ chối.");

            // Rule 12: Các grades phải là tiền tố liên tục của [A, B, C, D, E]
            // Hợp lệ: {A}, {A,B}, {A,B,C}, {A,B,C,D}, {A,B,C,D,E}
            // Không hợp lệ: {B,C}, {A,C,D}, {C,D,E} (thiếu grade đầu hoặc bỏ hạng giữa)
            {
                var gradePrefixes = new[] { "A", "B", "C", "D", "E" };
                var usedGrades = rules
                    .Select(r => r.Grade.Trim().ToUpperInvariant())
                    .OrderBy(g => Array.IndexOf(gradePrefixes, g))
                    .ToList();

                // Grade đầu tiên phải là A
                if (usedGrades[0] != "A")
                    throw new BadRequestException(
                        $"Tiêu chí '{code}': hạng đầu tiên phải là A (chất lượng tốt nhất). " +
                        $"Không thể bắt đầu từ hạng '{usedGrades[0]}'. " +
                        "Lý do: nếu kết quả kiểm định rất tốt, hệ thống không biết xếp hạng gì.");

                // Các grades phải liên tục (không bỏ hạng giữa)
                for (var i = 0; i < usedGrades.Count; i++)
                {
                    var expected = gradePrefixes[i];
                    if (usedGrades[i] != expected)
                        throw new BadRequestException(
                            $"Tiêu chí '{code}': thiếu hạng '{expected}' — các hạng phải liên tục (A→B→C...). " +
                            $"Không thể có hạng '{usedGrades[i]}' mà bỏ qua hạng '{expected}'.");
                }
            }

            // Rule 13: NUMBER — khoảng [Min, Max] của các grades không được chồng lấp
            // Vi phạm: grade A [0–14] và grade B [13–15] → chồng lấp tại [13, 14]
            if (dataType == "NUMBER" && rules.Count > 1)
            {
                var sortedRules = rules
                    .Select(r => new
                    {
                        r.Grade,
                        Min = r.MinValue,
                        Max = r.MaxValue
                    })
                    .ToList();

                for (var i = 0; i < sortedRules.Count; i++)
                {
                    for (var j = i + 1; j < sortedRules.Count; j++)
                    {
                        var ri = sortedRules[i];
                        var rj = sortedRules[j];

                        // Kiểm tra overlap giữa ri và rj:
                        // Overlap xảy ra khi: ri.Max >= rj.Min  VÀ  rj.Max >= ri.Min
                        // Xử lý cạnh biên: null Min = "-∞" (tất cả giá trị nhỏ hơn Max)
                        //                    null Max = "+∞" (tất cả giá trị lớn hơn Min)
                        var riEffectiveMin = ri.Min ?? decimal.MinValue;
                        var riEffectiveMax = ri.Max ?? decimal.MaxValue;
                        var rjEffectiveMin = rj.Min ?? decimal.MinValue;
                        var rjEffectiveMax = rj.Max ?? decimal.MaxValue;

                        var overlaps = riEffectiveMax >= rjEffectiveMin
                                    && rjEffectiveMax >= riEffectiveMin;

                        if (overlaps)
                            throw new BadRequestException(
                                $"Tiêu chí '{code}': hạng {ri.Grade} và hạng {rj.Grade} có khoảng giá trị chồng lấp nhau. " +
                                "Mỗi giá trị đo được chỉ được xếp vào đúng 1 hạng.");
                    }
                }
            }
            // Rule 14: CASCADE — Nếu hạng X là ngưỡng từ chối, mọi hạng tệ hơn X phải cũng là ngưỡng từ chối
            // Lý do: Nếu không chấp nhận hạng D, thì hạng E tệ hơn đương nhiên cũng không chấp nhận
            {
                var gradePrefixesForCascade = new[] { "A", "B", "C", "D", "E" };
                var failRuleGrades = rules
                    .Where(r => r.IsFailRule)
                    .Select(r => r.Grade.Trim().ToUpperInvariant())
                    .ToHashSet();

                if (failRuleGrades.Count > 0)
                {
                    // Tìm hạng đầu tiên (tốt nhất) là ngưỡng từ chối
                    var firstFailIndex = gradePrefixesForCascade
                        .Select((g, i) => new { g, i })
                        .Where(x => failRuleGrades.Contains(x.g))
                        .Select(x => x.i)
                        .Min();

                    // Tất cả các hạng sau đó (tệ hơn) phải là ngưỡng từ chối
                    for (var i = firstFailIndex + 1; i < gradePrefixesForCascade.Length; i++)
                    {
                        var expectedGrade = gradePrefixesForCascade[i];
                        // Chỉ kiểm tra các hạng đã được định nghĩa trong tiêu chí
                        var ruleExists = rules.Any(r => r.Grade.Trim().ToUpperInvariant() == expectedGrade);
                        if (ruleExists && !failRuleGrades.Contains(expectedGrade))
                            throw new BadRequestException(
                                $"Tiêu chí '{code}': Hạng {gradePrefixesForCascade[firstFailIndex]} là ngưỡng từ chối " +
                                $"nhưng hạng {expectedGrade} (tệ hơn) lại không phải. " +
                                "Logic sai: mức chất lượng tệ hơn bắt buộc cũng phải là ngưỡng từ chối (cascade).");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds an InspectionCriterion entity with multiple CriterionGradeRule children.
    /// BOOLEAN criteria: grade rules are optional; the engine evaluates BooleanValue directly.
    /// NUMBER criteria: each grade rule defines a [Min, Max] range.
    /// TEXT criteria: each grade rule defines an accepted RequiredTextValue.
    /// </summary>
    private static InspectionCriterion BuildCriterion(SaveInspectionCriterionRequest item)
    {
        var criterion = new InspectionCriterion
        {
            CriterionCode  = item.Code.Trim().ToUpperInvariant(),
            CriterionName  = item.Name.Trim(),
            CriterionGroup = string.IsNullOrWhiteSpace(item.CriterionGroup) ? "SENSORY" : item.CriterionGroup.Trim().ToUpperInvariant(),
            DataType       = item.DataType.Trim().ToUpperInvariant(),
            Unit           = string.IsNullOrWhiteSpace(item.Unit) ? null : item.Unit.Trim(),
            IsRequired     = item.IsRequired,
            IsCritical     = item.IsCritical
        };

        if (item.GradeRules is { Count: > 0 })
        {
            foreach (var rule in item.GradeRules)
            {
                criterion.GradeRules.Add(new CriterionGradeRule
                {
                    Grade             = rule.Grade.Trim().ToUpperInvariant(),
                    MinValue          = rule.MinValue,
                    MaxValue          = rule.MaxValue,
                    RequiredTextValue = string.IsNullOrWhiteSpace(rule.RequiredTextValue) ? null : rule.RequiredTextValue.Trim(),
                    IsFailRule        = rule.IsFailRule
                });
            }
        }

        return criterion;
    }

    /// <summary>Maps an InspectionCriterion entity → CriterionDto with full grade rules list.</summary>
    private static CriterionDto MapCriterion(InspectionCriterion c)
    {
        var gradeRules = c.GradeRules
            .OrderBy(r => r.Grade)
            .Select(r => new GradeRuleDto(
                r.CriterionGradeRuleId,
                r.Grade,
                r.MinValue,
                r.MaxValue,
                r.RequiredTextValue,
                r.IsFailRule))
            .ToList();

        return new CriterionDto(
            c.InspectionCriterionId,
            c.CriterionCode,
            c.CriterionName,
            c.CriterionGroup,
            c.DataType,
            c.Unit,
            c.IsRequired,
            c.IsCritical,
            gradeRules);
    }
}
