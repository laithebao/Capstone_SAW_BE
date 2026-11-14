using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.DistributorOrders;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.AiChat;

/// <summary>
/// Executes AI tool calls by querying the real database.
/// Batch and catalog lookups use the authenticated distributor's accountId for access checks.
/// </summary>
public sealed class AiToolExecutor(AppDbContext db, ILogger<AiToolExecutor> logger, KnowledgeBaseService knowledgeBase,
    IDistributorOrderService distributorOrders)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const int CatalogPageSize = 10;

    public async Task<string> ExecuteAsync(string toolName, JsonNode? args, int accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return toolName switch
            {
                "GetBatchSummary" => await GetBatchSummaryAsync(GetBatchIdentifier(args), accountId, ct),
                "GetBatchQcReport" => await GetBatchQcReportAsync(GetBatchIdentifier(args), accountId, ct),
                "SearchSaleLots" => await SearchSaleLotsAsync(args, accountId, ct),
                "GetStorageAdvice" => await GetStorageAdviceAsync(GetCropTypeId(args), ct),
                "SearchKnowledgeBase" => await knowledgeBase.SearchAsync(GetQuery(args), ct),
                _ => JsonSerializer.Serialize(new { error = $"Tool '{toolName}' không được hỗ trợ." })
            };
        }
        catch (ToolArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Tool {Tool} không xử lý được ({ErrorType}).", toolName, ex.GetType().Name);
            return """{"error":"Không thể tra cứu dữ liệu lúc này. Vui lòng thử lại sau."}""";
        }
    }

    private async Task<string> SearchSaleLotsAsync(JsonNode? args, int accountId, CancellationToken ct)
    {
        if (args is not JsonObject obj
            || obj["search"] is not JsonValue searchValue || !searchValue.TryGetValue<string>(out var search)
            || search.Length > 100 || search.Any(char.IsControl)
            || obj["page"] is not JsonValue pageValue || !pageValue.TryGetValue<int>(out var page)
            || page < 1 || page - 1 > int.MaxValue / CatalogPageSize)
            throw new ToolArgumentException("Từ khóa hoặc số trang tra cứu lô đang bán không hợp lệ.");

        var sort = obj["sort"] is JsonValue sortValue && sortValue.TryGetValue<string>(out var order)
            ? order switch
            {
                "newest" => CatalogSort.Newest,
                "price_asc" => CatalogSort.PriceAscending,
                "price_desc" => CatalogSort.PriceDescending,
                _ => throw new ToolArgumentException("Thứ tự tra cứu lô đang bán không hợp lệ.")
            }
            : throw new ToolArgumentException("Thiếu thứ tự tra cứu lô đang bán.");

        var result = await distributorOrders.CatalogAsync(accountId,
            new DistributorQuery(Search: search.Trim(), Page: page, PageSize: CatalogPageSize), ct, sort);

        return JsonSerializer.Serialize(new
        {
            totalCount = result.TotalCount,
            page = result.Page,
            pageSize = result.PageSize,
            currency = "VND",
            items = result.Items.Select(lot => new
            {
                batchCode = lot.BatchCode,
                productName = lot.ProductName,
                cropName = lot.CropName,
                origin = lot.Origin,
                qualityGrade = lot.QualityGrade,
                expiryDate = lot.ExpiryDate,
                weightKg = lot.WeightKg,
                wholeLotPrice = lot.WholeLotPrice
            })
        });
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

    private static string GetBatchIdentifier(JsonNode? args)
    {
        if (args is not JsonObject obj)
            throw new ToolArgumentException("Thiếu tham số mã lô hàng.");

        var node = obj["batchCode"] ?? obj["batchId"] ?? obj["code"] ?? obj["id"];
        if (node is null)
            throw new ToolArgumentException("Thiếu tham số batchCode.");

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && text.Length <= 200)
                return text.Trim().TrimStart('#');
            if (value.TryGetValue<long>(out var id) && id > 0)
                return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new ToolArgumentException("Mã lô hàng không hợp lệ.");
    }

    private static int GetCropTypeId(JsonNode? args)
    {
        if ((args as JsonObject)?["cropTypeId"] is not { } node)
            throw new ToolArgumentException("Thiếu tham số: cropTypeId");
        if (node is not JsonValue value || !value.TryGetValue<int>(out var id) || id <= 0)
            throw new ToolArgumentException("cropTypeId phải là số nguyên dương.");
        return id;
    }

    private static string GetQuery(JsonNode? args)
    {
        if ((args as JsonObject)?["query"] is not { } node)
            throw new ToolArgumentException("Thiếu tham số: query");
        if (node is not JsonValue value || !value.TryGetValue<string>(out var query)
            || string.IsNullOrWhiteSpace(query) || query.Trim().Length > Rag.OpenAIEmbeddingService.MaxInputChars)
            throw new ToolArgumentException("Nội dung tra cứu không hợp lệ.");
        return query.Trim();
    }

    private sealed class ToolArgumentException(string message) : Exception(message);
}
