using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.SpoilageForecast;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.AiForecast;

public sealed class SpoilageForecastGeminiService(
    AppDbContext db,
    GeminiApiClient geminiClient,
    ForecastInputCollector inputCollector,
    GeminiOptions options,
    ILogger<SpoilageForecastGeminiService> logger) : ISpoilageForecastService
{
    private static readonly TimeSpan CacheValidityDuration = TimeSpan.FromHours(6);

    public async Task<SpoilageForecastResponse> GenerateForecastAsync(
        int requestedByAccountId,
        SpoilageForecastRequest request,
        CancellationToken ct = default)
    {
        // 1. Kiểm tra cache nếu không yêu cầu làm mới bắt buộc
        if (!request.ForceRefresh)
        {
            var cached = await db.AiRiskForecasts
                .AsNoTracking()
                .Include(f => f.ProductBatch)
                    .ThenInclude(b => b.CropType)
                .Where(f => f.ProductBatchId == request.ProductBatchId)
                .OrderByDescending(f => f.GeneratedAt)
                .FirstOrDefaultAsync(ct);

            if (cached != null && DateTime.UtcNow - cached.GeneratedAt < CacheValidityDuration)
            {
                logger.LogInformation(
                    "[AI-Forecast] Trả về kết quả dự báo từ Cache cho Lô hàng ID {BatchId} (đã tạo cách đây {Minutes:F0} phút)",
                    request.ProductBatchId, (DateTime.UtcNow - cached.GeneratedAt).TotalMinutes);

                return MapEntityToResponse(cached, isFromCache: true);
            }
        }

        // 2. Thu thập dữ liệu đa chiều từ Database
        var snapshot = await inputCollector.CollectAsync(request.ProductBatchId, ct);
        if (snapshot == null)
        {
            logger.LogWarning("[AI-Forecast] Không tìm thấy lô hàng với ID {BatchId}", request.ProductBatchId);
            return CreateErrorResponse(request.ProductBatchId, "Không tìm thấy thông tin lô hàng trên hệ thống.");
        }

        // ── Kiểm tra điều kiện nghiệp vụ: Chỉ dự báo cho các lô ĐÃ NHẬP KHO và CHƯA XUẤT KHO ──
        var status = snapshot.Batch.BatchStatus?.Trim().ToUpperInvariant() ?? "";

        var preStorageStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PENDING_PREDECLARATION", "SUBMITTED", "PENDING_APPROVAL", "PENDING_QC",
            "RECEIVING", "IN_QC", "REJECTED", "QC_FAILED", "QUARANTINE"
        };

        var postStorageStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ISSUED", "RELEASED", "SOLD_OUT", "DISPOSED", "CANCELLED", "CANCELED"
        };

        var validInWarehouseStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "APPROVED_FOR_STORAGE", "RECEIVED", "IN_STOCK", "RESERVED", "PARTIALLY_ISSUED", "STORED", "APPROVED"
        };

        if (preStorageStatuses.Contains(status))
        {
            logger.LogInformation("[AI-Forecast] Từ chối dự báo lô {BatchCode} vì chưa nhập kho (Status: {Status})", snapshot.Batch.BatchCode, status);
            return CreateErrorResponse(
                request.ProductBatchId,
                $"Lô hàng '{snapshot.Batch.BatchCode}' chưa hoàn tất nhập kho (Trạng thái: {status}). Chức năng dự báo rủi ro hao hụt AI chỉ áp dụng cho các lô hàng đã kiểm định và nhập kho bảo quản thành công.",
                snapshot);
        }

        if (postStorageStatuses.Contains(status))
        {
            logger.LogInformation("[AI-Forecast] Từ chối dự báo lô {BatchCode} vì đã xuất kho/hủy (Status: {Status})", snapshot.Batch.BatchCode, status);
            return CreateErrorResponse(
                request.ProductBatchId,
                $"Lô hàng '{snapshot.Batch.BatchCode}' đã xuất kho hoặc kết thúc lưu trữ (Trạng thái: {status}). Không còn tồn kho để thực hiện dự báo hao hụt.",
                snapshot);
        }

        if (!validInWarehouseStatuses.Contains(status))
        {
            return CreateErrorResponse(
                request.ProductBatchId,
                $"Lô hàng '{snapshot.Batch.BatchCode}' không ở trạng thái lưu kho hợp lệ để dự báo hao hụt (Trạng thái: {status}).",
                snapshot);
        }

        // 3. Xây dựng prompt chuyên môn
        var systemPrompt = ForecastPromptBuilder.BuildSystemPrompt();
        var userPrompt = ForecastPromptBuilder.BuildUserPrompt(snapshot);

        // 4. Gọi Gemini API để sinh kết quả JSON có cấu trúc
        string rawJson;
        try
        {
            logger.LogInformation("[AI-Forecast] Bắt đầu gọi Gemini ({Model}) dự báo hao hụt cho lô {BatchCode}",
                options.Model, snapshot.Batch.BatchCode);

            rawJson = await geminiClient.GenerateStructuredJsonAsync(systemPrompt, userPrompt, ct);
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("[AI-Forecast] Hết thời gian chờ phản hồi từ Gemini API cho lô {BatchCode}", snapshot.Batch.BatchCode);
            return CreateErrorResponse(request.ProductBatchId, "Hết thời gian chờ phản hồi từ Gemini AI. Vui lòng thử lại sau.", snapshot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AI-Forecast] Lỗi khi kết nối hoặc xử lý với Gemini API cho lô {BatchCode}", snapshot.Batch.BatchCode);
            return CreateErrorResponse(request.ProductBatchId, $"Lỗi kết nối Gemini AI: {ex.Message}", snapshot);
        }

        // 5. Phân tích kết quả trả về từ AI
        var parseResult = ForecastResponseParser.Parse(rawJson);
        if (!parseResult.IsSuccess || parseResult.Data == null)
        {
            logger.LogError("[AI-Forecast] Phân tích cú pháp JSON thất bại: {Error}. Raw: {Raw}", parseResult.ErrorMessage, rawJson);
            return CreateErrorResponse(request.ProductBatchId, "AI trả về dữ liệu không đúng định dạng. Vui lòng thử lại.", snapshot);
        }

        var parsed = parseResult.Data;

        var rawRecommendation = parsed.Recommendation ?? "";
        var truncatedRecommendation = rawRecommendation.Length > 1490
            ? rawRecommendation[..1487] + "..."
            : rawRecommendation;

        // 6. Đóng gói snapshot đầu vào + các hành động chi tiết vào InputSnapshotJson để lưu trữ đầy đủ
        var snapshotWrapper = new
        {
            batchCode = snapshot.Batch.BatchCode,
            harvestDate = snapshot.Batch.HarvestDate,
            daysFromHarvest = snapshot.DaysFromHarvest,
            daysUntilExpiry = snapshot.DaysUntilExpiry,
            shelfLifeConsumedPct = snapshot.ShelfLifeConsumedPct,
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
            fullRecommendation = rawRecommendation
        };

        var snapshotJson = JsonSerializer.Serialize(snapshotWrapper);

        // 7. Lưu kết quả vào bảng AI_RISK_FORECAST
        var entity = new AiRiskForecast
        {
            ProductBatchId = request.ProductBatchId,
            SpoilageRiskScore = parsed.SpoilageRiskScore,
            PredictedLossPercent = parsed.PredictedLossPercent,
            SafeStorageRemainingDays = parsed.SafeStorageRemainingDays,
            RiskLevel = parsed.RiskLevel,
            ForecastHorizonDays = parsed.ForecastHorizonDays,
            Recommendation = truncatedRecommendation,
            ModelVersion = options.Model,
            InputSnapshotJson = snapshotJson,
            GeneratedAt = DateTime.UtcNow
        };

        db.AiRiskForecasts.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[AI-Forecast] Đã lưu dự báo thành công cho lô {BatchCode}: Risk={RiskLevel}, Score={Score}, Loss={Loss}%",
            snapshot.Batch.BatchCode, entity.RiskLevel, entity.SpoilageRiskScore, entity.PredictedLossPercent);

        return new SpoilageForecastResponse(
            ProductBatchId: entity.ProductBatchId,
            BatchCode: snapshot.Batch.BatchCode,
            ProductName: snapshot.Batch.ProductName,
            CropName: snapshot.Batch.CropType?.CropName ?? "N/A",
            QualityGrade: snapshot.Batch.QualityGrade,
            SpoilageRiskScore: entity.SpoilageRiskScore,
            PredictedLossPercent: entity.PredictedLossPercent,
            SafeStorageRemainingDays: entity.SafeStorageRemainingDays,
            RiskLevel: entity.RiskLevel,
            ForecastHorizonDays: entity.ForecastHorizonDays,
            Recommendation: rawRecommendation,
            Actions: parsed.Actions,
            AnalysisNotes: parsed.AnalysisNotes,
            GeneratedAt: ToVietnamTime(entity.GeneratedAt),
            IsFromCache: false,
            ModelVersion: entity.ModelVersion ?? options.Model
        );
    }

    public async Task<SpoilageForecastResponse?> GetLatestForecastAsync(long productBatchId, CancellationToken ct = default)
    {
        var entity = await db.AiRiskForecasts
            .AsNoTracking()
            .Include(f => f.ProductBatch)
                .ThenInclude(b => b.CropType)
            .Where(f => f.ProductBatchId == productBatchId)
            .OrderByDescending(f => f.GeneratedAt)
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
            .Take(Math.Min(count, 20))
            .ToListAsync(ct);

        return entities.Select(e => MapEntityToResponse(e, isFromCache: false)).ToList();
    }

    public async Task<IReadOnlyList<SpoilageForecastResponse>> GetHighRiskBatchesAsync(
        int limit = 10,
        CancellationToken ct = default)
    {
        var activeStorageStatuses = new[]
        {
            "APPROVED_FOR_STORAGE", "RECEIVED", "IN_STOCK", "RESERVED", "PARTIALLY_ISSUED", "STORED", "APPROVED"
        };

        // Lấy forecast mới nhất của mỗi lô hàng đang lưu kho có RiskLevel là HIGH hoặc CRITICAL
        var highRiskBatchIds = await db.AiRiskForecasts
            .AsNoTracking()
            .Where(f => (f.RiskLevel == "HIGH" || f.RiskLevel == "CRITICAL") &&
                        activeStorageStatuses.Contains(f.ProductBatch.BatchStatus))
            .GroupBy(f => f.ProductBatchId)
            .Select(g => g.OrderByDescending(f => f.GeneratedAt).First().AiRiskForecastId)
            .Take(limit * 2)
            .ToListAsync(ct);

        var entities = await db.AiRiskForecasts
            .AsNoTracking()
            .Include(f => f.ProductBatch)
                .ThenInclude(b => b.CropType)
            .Where(f => highRiskBatchIds.Contains(f.AiRiskForecastId))
            .OrderByDescending(f => f.SpoilageRiskScore)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(e => MapEntityToResponse(e, isFromCache: true)).ToList();
    }

    private static SpoilageForecastResponse MapEntityToResponse(AiRiskForecast entity, bool isFromCache)
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

        return new SpoilageForecastResponse(
            ProductBatchId: entity.ProductBatchId,
            BatchCode: entity.ProductBatch?.BatchCode ?? "",
            ProductName: entity.ProductBatch?.ProductName ?? "",
            CropName: entity.ProductBatch?.CropType?.CropName ?? "N/A",
            QualityGrade: entity.ProductBatch?.QualityGrade,
            SpoilageRiskScore: entity.SpoilageRiskScore,
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

    private static SpoilageForecastResponse CreateErrorResponse(
        long batchId,
        string message,
        ForecastInputSnapshot? snapshot = null)
    {
        return new SpoilageForecastResponse(
            ProductBatchId: batchId,
            BatchCode: snapshot?.Batch.BatchCode ?? "",
            ProductName: snapshot?.Batch.ProductName ?? "",
            CropName: snapshot?.Batch.CropType?.CropName ?? "",
            QualityGrade: snapshot?.Batch.QualityGrade,
            SpoilageRiskScore: 0,
            PredictedLossPercent: null,
            SafeStorageRemainingDays: null,
            RiskLevel: "UNKNOWN",
            ForecastHorizonDays: null,
            Recommendation: message,
            Actions: [],
            AnalysisNotes: null,
            GeneratedAt: ToVietnamTime(DateTime.UtcNow),
            IsFromCache: false,
            ModelVersion: "",
            IsError: true,
            ErrorMessage: message
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
