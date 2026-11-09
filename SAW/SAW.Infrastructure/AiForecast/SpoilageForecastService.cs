using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.SpoilageForecast;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.AiForecast;

public sealed class SpoilageForecastService(
    AppDbContext db,
    IForecastModelClient forecastClient,
    ForecastInputCollector inputCollector,
    ILogger<SpoilageForecastService> logger) : ISpoilageForecastService
{
    // Bound each structured response while covering all eligible batches in one run.
    private const int BatchesPerModelRequest = 6;

    public async Task<(IReadOnlyList<SpoilageForecastResponse> Forecasts, string? ErrorMessage)> GenerateForecastsAsync(
        CancellationToken ct = default)
    {
        var generatedAt = DateTime.UtcNow;
        var batches = await inputCollector.ListEligibleBatchesAsync(ct);
        var generated = new List<(AiRiskForecast Entity, ProductBatch Batch)>(batches.Count);
        var systemPrompt = ForecastPromptBuilder.BuildSystemPrompt();

        try
        {
            foreach (var group in batches.Chunk(BatchesPerModelRequest))
            {
                var snapshots = new List<ForecastInputSnapshot>(group.Length);
                foreach (var batch in group)
                {
                    snapshots.Add(await inputCollector.CollectAsync(batch, ct));
                }

                var userPrompt = ForecastPromptBuilder.BuildUserPrompt(snapshots);
                var (json, model) = await forecastClient.GenerateAsync(systemPrompt, userPrompt, ct);
                var result = ForecastResponseParser.Parse(json, group.Select(b => b.ProductBatchId));
                if (result.Data is null)
                {
                    logger.LogWarning("[AI-Forecast] Kết quả dự báo không hợp lệ: {Reason}", result.ErrorMessage);
                    return ([], "AI trả về dự báo thiếu hoặc không hợp lệ. Vui lòng thử lại.");
                }

                foreach (var snapshot in snapshots)
                {
                    var batchId = snapshot.Batch.ProductBatchId;
                    var entity = CreateEntity(snapshot, result.Data[batchId], model, generatedAt);
                    generated.Add((entity, snapshot.Batch));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("[AI-Forecast] Hết thời gian chờ phản hồi từ OpenAI.");
            return ([], "Hết thời gian chờ dự báo AI. Vui lòng thử lại sau.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("[AI-Forecast] Không thể lấy dự báo từ OpenAI.");
            return ([], "Dịch vụ dự báo AI tạm thời gián đoạn. Vui lòng thử lại sau.");
        }
        catch (JsonException)
        {
            logger.LogWarning("[AI-Forecast] OpenAI không trả về kết quả dự báo hoàn chỉnh.");
            return ([], "AI chưa tạo được dự báo. Vui lòng thử lại.");
        }

        if (generated.Count == 0)
            return ([], null);

        db.AiRiskForecasts.AddRange(generated.Select(item => item.Entity));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("[AI-Forecast] Đã lưu dự báo cho {BatchCount} lô hàng.", generated.Count);

        return (generated.Select(item => MapEntityToResponse(item.Entity, isFromCache: false, item.Batch)).ToList(), null);
    }

    private static AiRiskForecast CreateEntity(
        ForecastInputSnapshot snapshot, ForecastParsedData parsed, string model, DateTime generatedAt)
    {
        var recommendation = parsed.Recommendation;
        var snapshotJson = JsonSerializer.Serialize(new
        {
            batchCode = snapshot.Batch.BatchCode,
            harvestDate = snapshot.Batch.HarvestDate,
            daysFromHarvest = snapshot.DaysFromHarvest,
            daysUntilExpiry = snapshot.DaysUntilExpiry,
            shelfLifeConsumedPct = snapshot.ShelfLifeConsumedPct,
            quantityOnHandKg = snapshot.QuantityOnHandKg,
            qcSummary = snapshot.QcInspection != null ? new
            {
                inspectionCode = snapshot.QcInspection.InspectionCode,
                qcResult = snapshot.QcInspection.QcResult,
                grade = snapshot.QcInspection.QualityGrade,
                damagePercentage = snapshot.QcInspection.SensoryResult?.DamagePercentage
            } : null,
            envSummary = snapshot.EnvStats,
            actions = parsed.Actions,
            analysisNotes = parsed.AnalysisNotes,
            fullRecommendation = recommendation
        });

        return new AiRiskForecast
        {
            ProductBatchId = snapshot.Batch.ProductBatchId,
            PredictedLossPercent = parsed.PredictedLossPercent,
            SafeStorageRemainingDays = parsed.SafeStorageRemainingDays,
            RiskLevel = parsed.RiskLevel,
            ForecastHorizonDays = parsed.ForecastHorizonDays,
            Recommendation = recommendation.Length > 1500 ? recommendation[..1497] + "..." : recommendation,
            ModelVersion = model,
            InputSnapshotJson = snapshotJson,
            GeneratedAt = generatedAt
        };
    }

    public async Task<IReadOnlyList<SpoilageForecastResponse>> GetLatestForecastsAsync(CancellationToken ct = default)
    {
        var entities = await LatestEligibleForecasts()
            .OrderBy(f => f.ProductBatchId)
            .ToListAsync(ct);

        return entities.Select(e => MapEntityToResponse(e, isFromCache: true)).ToList();
    }

    public async Task<SpoilageForecastResponse?> GetLatestForecastAsync(long productBatchId, CancellationToken ct = default)
    {
        var entity = await db.AiRiskForecasts
            .AsNoTracking()
            .Include(f => f.ProductBatch)
                .ThenInclude(b => b.CropType)
            .Where(f => f.ProductBatchId == productBatchId)
            .OrderByDescending(f => f.GeneratedAt)
            .ThenByDescending(f => f.AiRiskForecastId)
            .FirstOrDefaultAsync(ct);

        return entity == null ? null : MapEntityToResponse(entity, isFromCache: true);
    }

    public async Task<IReadOnlyList<SpoilageForecastResponse>> GetForecastHistoryAsync(
        long productBatchId,
        int count = 5,
        CancellationToken ct = default)
    {
        var entities = await db.AiRiskForecasts
            .AsNoTracking()
            .Include(f => f.ProductBatch)
                .ThenInclude(b => b.CropType)
            .Where(f => f.ProductBatchId == productBatchId)
            .OrderByDescending(f => f.GeneratedAt)
            .ThenByDescending(f => f.AiRiskForecastId)
            .Take(Math.Min(count, 20))
            .ToListAsync(ct);

        return entities.Select(e => MapEntityToResponse(e, isFromCache: false)).ToList();
    }

    public async Task<IReadOnlyList<SpoilageForecastResponse>> GetHighRiskBatchesAsync(
        int limit = 10,
        CancellationToken ct = default)
    {
        var entities = await LatestEligibleForecasts()
            .Where(f => f.RiskLevel == "HIGH" || f.RiskLevel == "CRITICAL")
            .OrderByDescending(f => f.RiskLevel == "CRITICAL")
            .ThenByDescending(f => f.PredictedLossPercent)
            .ThenBy(f => f.ProductBatchId)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(e => MapEntityToResponse(e, isFromCache: true)).ToList();
    }

    private IQueryable<AiRiskForecast> LatestEligibleForecasts()
    {
        var eligible = inputCollector.EligibleBatches;
        return db.AiRiskForecasts
            .AsNoTracking()
            .Include(f => f.ProductBatch)
                .ThenInclude(b => b.CropType)
            .Where(f => eligible.Any(b => b.ProductBatchId == f.ProductBatchId)
                && f.AiRiskForecastId == f.ProductBatch.AiRiskForecasts
                    .OrderByDescending(latest => latest.GeneratedAt)
                    .ThenByDescending(latest => latest.AiRiskForecastId)
                    .Select(latest => latest.AiRiskForecastId)
                    .First());
    }

    private static SpoilageForecastResponse MapEntityToResponse(
        AiRiskForecast entity, bool isFromCache, ProductBatch? batch = null)
    {
        var actions = new List<ForecastActionItem>();
        string? notes = null;
        var recommendation = entity.Recommendation ?? "";

        if (!string.IsNullOrWhiteSpace(entity.InputSnapshotJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(entity.InputSnapshotJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("fullRecommendation", out var fullRecEl) && fullRecEl.ValueKind == JsonValueKind.String)
                {
                    var fullRec = fullRecEl.GetString();
                    if (!string.IsNullOrWhiteSpace(fullRec))
                    {
                        recommendation = fullRec;
                    }
                }

                if (root.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in actionsEl.EnumerateArray())
                    {
                        var priority = item.TryGetProperty("Priority", out var p1) ? p1.GetString() : item.TryGetProperty("priority", out var p2) ? p2.GetString() : "RECOMMENDED";
                        var category = item.TryGetProperty("Category", out var c1) ? c1.GetString() : item.TryGetProperty("category", out var c2) ? c2.GetString() : "OTHER";
                        var action = item.TryGetProperty("Action", out var a1) ? a1.GetString() : item.TryGetProperty("action", out var a2) ? a2.GetString() : "";
                        var reason = item.TryGetProperty("Reason", out var r1) ? r1.GetString() : item.TryGetProperty("reason", out var r2) ? r2.GetString() : "";
                        var deadline = item.TryGetProperty("Deadline", out var d1) ? d1.GetString() : item.TryGetProperty("deadline", out var d2) ? d2.GetString() : null;

                        if (!string.IsNullOrWhiteSpace(action))
                        {
                            actions.Add(new ForecastActionItem(priority ?? "RECOMMENDED", category ?? "OTHER", action, reason ?? "", deadline));
                        }
                    }
                }

                if (root.TryGetProperty("analysisNotes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
                {
                    notes = notesEl.GetString();
                }
            }
            catch
            {
                // Bỏ qua lỗi parse JSON phụ trợ
            }
        }

        batch ??= entity.ProductBatch;
        return new SpoilageForecastResponse(
            ProductBatchId: entity.ProductBatchId,
            BatchCode: batch?.BatchCode ?? "",
            ProductName: batch?.ProductName ?? "",
            CropName: batch?.CropType?.CropName ?? "N/A",
            QualityGrade: batch?.QualityGrade,
            PredictedLossPercent: entity.PredictedLossPercent,
            SafeStorageRemainingDays: entity.SafeStorageRemainingDays,
            RiskLevel: entity.RiskLevel,
            ForecastHorizonDays: entity.ForecastHorizonDays,
            Recommendation: recommendation,
            Actions: actions,
            AnalysisNotes: notes,
            GeneratedAt: ToVietnamTime(entity.GeneratedAt),
            IsFromCache: isFromCache,
            ModelVersion: entity.ModelVersion ?? ""
        );
    }

    /// <summary>
    /// Chuyển đổi DateTime lưu dạng UTC sang DateTimeOffset chuẩn giờ Việt Nam (UTC+7).
    /// Giúp API JSON trả về offset +07:00 rõ ràng để giao diện Frontend luôn hiển thị đúng giờ Việt Nam.
    /// </summary>
    private static DateTimeOffset ToVietnamTime(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc
            ? dt
            : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        return new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(7));
    }
}
