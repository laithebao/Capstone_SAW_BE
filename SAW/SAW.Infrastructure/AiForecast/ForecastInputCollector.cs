using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.AiForecast;

/// <summary>
/// Thu thập toàn bộ dữ liệu lịch sử và hiện tại của lô hàng để AI đánh giá rủi ro hao hụt:
/// - Thông tin cơ bản và sinh lý của loại cây trồng
/// - Kết quả kiểm định chất lượng (cảm quan, hóa nghiệm)
/// - Nhật ký điều kiện môi trường thực tế (nhiệt độ, độ ẩm 72 giờ gần nhất)
/// - Vị trí lưu kho và dung sai thiết kế
/// </summary>
public sealed class ForecastInputCollector(AppDbContext db)
{
    public async Task<ForecastInputSnapshot?> CollectAsync(long productBatchId, CancellationToken ct)
    {
        // 1. Thông tin lô hàng + Loại nông sản + Vùng trồng + Nhà cung cấp
        var batch = await db.ProductBatches
            .AsNoTracking()
            .Include(b => b.CropType)
            .Include(b => b.GrowingArea)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.ProductBatchId == productBatchId, ct);

        if (batch == null) return null;

        // 2. Kết quả kiểm định QC gần nhất đã hoàn tất
        var qcInspection = await db.QcInspections
            .AsNoTracking()
            .Include(q => q.SensoryResult)
            .Include(q => q.LabResult)
            .Include(q => q.ResultDetails)
                .ThenInclude(d => d.InspectionCriterion)
            .Where(q => q.ProductBatchId == productBatchId && q.InspectionStatus == "COMPLETED")
            .OrderByDescending(q => q.CompletedAt ?? q.StartedAt)
            .FirstOrDefaultAsync(ct);

        // 3. Nhật ký môi trường trong 72 giờ qua
        var cutoff = DateTime.UtcNow.AddHours(-72);
        var envLogs = await db.EnvironmentLogs
            .AsNoTracking()
            .Include(e => e.WarehouseLocation)
            .Where(e => e.ProductBatchId == productBatchId && e.RecordedAt >= cutoff)
            .OrderByDescending(e => e.RecordedAt)
            .Take(100)
            .ToListAsync(ct);

        // 4. Vị trí kho hiện tại của lô hàng
        var inventory = await db.Inventories
            .AsNoTracking()
            .Include(i => i.WarehouseLocation)
            .Where(i => i.ProductBatchId == productBatchId && i.QuantityOnHand > 0)
            .FirstOrDefaultAsync(ct);

        // 5. Tính toán các chỉ số thời gian
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysFromHarvest = Math.Max(0, today.DayNumber - batch.HarvestDate.DayNumber);
        
        int? daysUntilExpiry = batch.ExpiryDate.HasValue
            ? batch.ExpiryDate.Value.DayNumber - today.DayNumber
            : null;

        decimal? shelfLifeConsumedPct = null;
        var totalShelfLife = batch.ShelfLifeDaysSnapshot ?? batch.CropType?.ShelfLifeDays;
        if (totalShelfLife is > 0)
        {
            shelfLifeConsumedPct = Math.Round((decimal)daysFromHarvest / totalShelfLife.Value * 100m, 1);
        }

        // 6. Thống kê môi trường (Min, Max, Avg, số lần lệch ngưỡng)
        EnvironmentStats? envStats = null;
        if (envLogs.Count > 0)
        {
            var validTemps = envLogs.Select(e => e.TemperatureC).ToList();
            var validHumids = envLogs.Where(e => e.HumidityPct.HasValue).Select(e => e.HumidityPct!.Value).ToList();

            var minExpectedTemp = batch.ExpectedMinTempC ?? batch.CropType?.ExpectedMinTempC;
            var maxExpectedTemp = batch.ExpectedMaxTempC ?? batch.CropType?.ExpectedMaxTempC;

            var deviationCount = envLogs.Count(e =>
                (minExpectedTemp.HasValue && e.TemperatureC < minExpectedTemp.Value) ||
                (maxExpectedTemp.HasValue && e.TemperatureC > maxExpectedTemp.Value));

            envStats = new EnvironmentStats(
                AvgTempC: Math.Round(validTemps.Average(), 1),
                MinTempC: validTemps.Min(),
                MaxTempC: validTemps.Max(),
                AvgHumidityPct: validHumids.Count > 0 ? Math.Round(validHumids.Average(), 1) : null,
                TempDeviationCount: deviationCount,
                LogCount: envLogs.Count,
                PeriodHours: 72
            );
        }

        return new ForecastInputSnapshot(
            Batch: batch,
            QcInspection: qcInspection,
            EnvStats: envStats,
            RecentEnvLogs: envLogs.Take(10).ToList(),
            CurrentLocation: inventory?.WarehouseLocation,
            DaysFromHarvest: daysFromHarvest,
            DaysUntilExpiry: daysUntilExpiry,
            ShelfLifeConsumedPct: shelfLifeConsumedPct
        );
    }
}
