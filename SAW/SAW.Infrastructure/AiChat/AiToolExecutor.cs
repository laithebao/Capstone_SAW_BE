using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Executes AI tool calls by querying the real database.
/// All queries filter by the authenticated distributor's accountId to prevent data leakage.
/// </summary>
public sealed class AiToolExecutor(AppDbContext db, ILogger<AiToolExecutor> logger, KnowledgeBaseService knowledgeBase)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public async Task<string> ExecuteAsync(string toolName, JsonNode? args, int accountId, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "GetBatchSummary" => await GetBatchSummaryAsync(GetBatchIdentifier(args), accountId, ct),
                "GetBatchQcReport" => await GetBatchQcReportAsync(GetBatchIdentifier(args), accountId, ct),
                "GetStorageAdvice" => await GetStorageAdviceAsync((int)GetLong(args, "cropTypeId"), ct),
                "SearchKnowledgeBase" => await SearchKnowledgeBaseAsync(GetString(args, "query"), ct),
                _ => $"{{ \"error\": \"Tool '{toolName}' không được hỗ trợ.\" }}"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool {Tool} failed for account {AccountId}", toolName, accountId);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: GetBatchSummary ─────────────────────────────────────────
    private async Task<string> GetBatchSummaryAsync(string batchCodeOrId, int accountId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(batchCodeOrId))
            return """{ "found": false, "message": "Vui lòng cung cấp mã lô hàng." }""";

        long? parsedId = long.TryParse(batchCodeOrId, out var id) ? id : null;

        var batch = await db.ProductBatches
            .AsNoTracking()
            .Include(b => b.CropType)
            .Include(b => b.GrowingArea)
            .Include(b => b.Supplier)
            .Where(b => b.BatchCode == batchCodeOrId || (parsedId.HasValue && b.ProductBatchId == parsedId.Value))
            .Select(b => new
            {
                batchId = b.ProductBatchId,
                batchCode = b.BatchCode,
                productName = b.ProductName,
                cropName = b.CropType.CropName,
                categoryName = b.CropType.CategoryName,
                cropTypeId = b.CropTypeId,
                supplierName = b.Supplier.SupplierName,
                origin = b.GrowingArea.AreaName,
                province = b.GrowingArea.Province,
                district = b.GrowingArea.District,
                harvestDate = b.HarvestDate,
                expiryDate = b.ExpiryDate,
                weightKg = b.WeightInKg,
                batchStatus = b.BatchStatus,
                qualityGrade = b.QualityGrade,
                packagingType = b.PackagingType,
                minTempC = b.ExpectedMinTempC,
                maxTempC = b.ExpectedMaxTempC,
                minHumidityPct = b.ExpectedMinHumidityPct,
                maxHumidityPct = b.ExpectedMaxHumidityPct,
                shelfLifeDays = b.ShelfLifeDaysSnapshot,
                note = b.Note
            })
            .FirstOrDefaultAsync(ct);

        if (batch == null)
            return JsonSerializer.Serialize(new { found = false, message = $"Không tìm thấy lô hàng có mã '{batchCodeOrId}'." });

        var canAccess = await CanAccessBatchAsync(batch.batchId, accountId, ct);
        if (!canAccess)
            return """{ "found": false, "message": "Bạn không có quyền xem thông tin lô hàng này." }""";

        return JsonSerializer.Serialize(new { found = true, data = batch }, JsonOpts);
    }

    // ── Tool: GetBatchQcReport ────────────────────────────────────────
    private async Task<string> GetBatchQcReportAsync(string batchCodeOrId, int accountId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(batchCodeOrId))
            return """{ "found": false, "message": "Vui lòng cung cấp mã lô hàng." }""";

        long? parsedId = long.TryParse(batchCodeOrId, out var id) ? id : null;

        var batch = await db.ProductBatches
            .AsNoTracking()
            .Where(b => b.BatchCode == batchCodeOrId || (parsedId.HasValue && b.ProductBatchId == parsedId.Value))
            .Select(b => new { b.ProductBatchId, b.BatchCode, b.ProductName })
            .FirstOrDefaultAsync(ct);

        if (batch == null)
            return JsonSerializer.Serialize(new { found = false, message = $"Không tìm thấy lô hàng có mã '{batchCodeOrId}'." });

        var canAccess = await CanAccessBatchAsync(batch.ProductBatchId, accountId, ct);
        if (!canAccess)
            return """{ "found": false, "message": "Bạn không có quyền xem thông tin kiểm định lô hàng này." }""";

        var qc = await db.QcInspections
            .AsNoTracking()
            .Include(q => q.SensoryResult)
            .Include(q => q.LabResult)
            .Include(q => q.ResultDetails)
                .ThenInclude(d => d.InspectionCriterion)
            .Include(q => q.InspectionStandardVersion)
                .ThenInclude(v => v.InspectionStandardSet)
            .Where(q => q.ProductBatchId == batch.ProductBatchId && q.InspectionStatus == "COMPLETED")
            .OrderByDescending(q => q.CompletedAt)
            .Select(q => new
            {
                batchCode = batch.BatchCode,
                productName = batch.ProductName,
                inspectionCode = q.InspectionCode,
                qcResult = q.QcResult,
                qualityGrade = q.QualityGrade,
                completedAt = q.CompletedAt,
                standardName = q.InspectionStandardVersion.InspectionStandardSet.StandardName,
                standardVersionNo = q.InspectionStandardVersion.VersionNo,
                sensory = q.SensoryResult == null ? null : new
                {
                    freshnessScore = q.SensoryResult.FreshnessScore,
                    sizeScore = q.SensoryResult.SizeScore,
                    colorScore = q.SensoryResult.ColorScore,
                    ripenessScore = q.SensoryResult.RipenessScore,
                    damagePercentage = q.SensoryResult.DamagePercentage,
                    note = q.SensoryResult.Note
                },
                labResult = q.LabResult == null ? null : new
                {
                    chemicalResidueStatus = q.LabResult.ChemicalResidueStatus,
                    residueValue = q.LabResult.ResidueValue,
                    residueUnit = q.LabResult.ResidueUnit,
                    pathogenStatus = q.LabResult.PathogenStatus,
                    pathogenName = q.LabResult.PathogenName,
                    labName = q.LabResult.LabName,
                    testedAt = q.LabResult.TestedAt
                },
                criterionDetails = q.ResultDetails.Select(d => new
                {
                    criterionName = d.InspectionCriterion.CriterionName,
                    criterionGroup = d.InspectionCriterion.CriterionGroup,
                    unit = d.InspectionCriterion.Unit,
                    numericValue = d.NumericValue,
                    textValue = d.TextValue,
                    evaluatedGrade = d.EvaluatedGrade,
                    isPassed = d.IsPassed,
                    remarks = d.Remarks
                })
            })
            .FirstOrDefaultAsync(ct);

        if (qc == null)
            return JsonSerializer.Serialize(new { found = false, message = $"Lô hàng mã '{batch.BatchCode}' chưa có kết quả kiểm định hoàn chỉnh." });

        return JsonSerializer.Serialize(new { found = true, data = qc }, JsonOpts);
    }

    // ── Tool: SearchKnowledgeBase ─────────────────────────────────────
    private async Task<string> SearchKnowledgeBaseAsync(string query, CancellationToken ct)
    {
        logger.LogInformation("[AI-KB] SearchKnowledgeBase được gọi với query: '{Query}'", query);
        var result = await knowledgeBase.SearchAsync(query, ct);
        logger.LogInformation("[AI-KB] SearchKnowledgeBase trả về {Length} ký tự", result.Length);
        return result;
    }

    // ── Tool: GetStorageAdvice ────────────────────────────────────────
    private async Task<string> GetStorageAdviceAsync(int cropTypeId, CancellationToken ct)
    {
        var crop = await db.CropTypes
            .AsNoTracking()
            .Where(c => c.CropTypeId == cropTypeId)
            .Select(c => new
            {
                cropName = c.CropName,
                categoryName = c.CategoryName,
                minTempC = c.ExpectedMinTempC,
                maxTempC = c.ExpectedMaxTempC,
                minHumidityPct = c.ExpectedMinHumidityPct,
                maxHumidityPct = c.ExpectedMaxHumidityPct,
                shelfLifeDays = c.ShelfLifeDays,
                description = c.Description
            })
            .FirstOrDefaultAsync(ct);

        if (crop == null)
            return """{ "found": false, "message": "Không tìm thấy thông tin loại nông sản này." }""";

        return JsonSerializer.Serialize(new { found = true, data = crop }, JsonOpts);
    }

    // ── Shared visibility check ───────────────────────────────────────
    private async Task<bool> CanAccessBatchAsync(long batchId, int accountId, CancellationToken ct)
    {
        var catalogStatuses = new[]
        {
            "APPROVED", "APPROVED_FOR_STORAGE", "RECEIVED", "IN_STOCK",
            "RESERVED", "PARTIALLY_ISSUED", "ISSUED", "PARTIALLY_RELEASED",
            "RELEASED", "SOLD_OUT"
        };

        return await db.ProductBatches.AsNoTracking().AnyAsync(b =>
            b.ProductBatchId == batchId &&
            (catalogStatuses.Contains(b.BatchStatus) ||
             db.Distributors.Any(dist =>
                 dist.AccountId == accountId &&
                 dist.PurchaseOrders.Any(o =>
                     o.OrderDetails.Any(od => od.RequestedProductBatchId == batchId)))), ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────
    private static string GetBatchIdentifier(JsonNode? args)
    {
        if (args == null) throw new ArgumentException("Thiếu tham số mã lô hàng.");
        var val = args["batchCode"] ?? args["batchId"] ?? args["code"] ?? args["id"];
        if (val == null) throw new ArgumentException("Thiếu tham số batchCode.");
        var str = val.ToString()?.Trim() ?? string.Empty;
        return str.TrimStart('#');
    }

    private static long GetLong(JsonNode? args, string key)
    {
        var val = args?[key] ?? throw new ArgumentException($"Thiếu tham số: {key}");
        return val.GetValue<long>();
    }
    private static string GetString(JsonNode? args, string key)
    {
        var val = args?[key] ?? throw new ArgumentException($"Thiếu tham số: {key}");
        return val.GetValue<string>();
    }
}
