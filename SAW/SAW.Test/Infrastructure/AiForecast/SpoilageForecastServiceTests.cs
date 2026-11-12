using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiForecast;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.AiForecast;

public sealed class SpoilageForecastServiceTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static SpoilageForecastService Service(AppDbContext db, Mock<IForecastModelClient> model) =>
        new(db, model.Object, new ForecastInputCollector(db), NullLogger<SpoilageForecastService>.Instance);

    private static async Task<ProductBatch[]> SeedAsync(AppDbContext db, int count = 1)
    {
        var crop = new CropType { CropCode = "CROP", CropName = "Mango", CategoryName = "Fruit", ShelfLifeDays = 10 };
        var area = new GrowingArea { AreaName = "Area", Province = "Province", District = "District", Ward = "Ward", Region = "South" };
        var supplier = new Supplier { SupplierCode = "SUP", SupplierName = "Supplier", TaxCode = "0123456789", ContactPerson = "Contact", Address = "Address" };
        var location = new WarehouseLocation { LocationCode = "A1", ZoneName = "Zone" };
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var batches = Enumerable.Range(1, count).Select(id => new ProductBatch { ProductBatchId = id, BatchCode = $"B-{id}", ProductName = "Mango",
            CropType = crop, GrowingArea = area, Supplier = supplier, Unit = "kg", BatchStatus = "IN_STOCK", HarvestDate = today.AddDays(-3),
            ExpiryDate = today.AddDays(7), Inventories = [new Inventory { WarehouseLocation = location, QuantityOnHand = 100 }] }).ToArray();
        db.AddRange(batches); await db.SaveChangesAsync(); return batches;
    }

    private static AiRiskForecast Forecast(ProductBatch batch, long id, string risk, decimal loss, DateTime at, string? json = null) => new()
    { ProductBatch = batch, AiRiskForecastId = id, RiskLevel = risk, PredictedLossPercent = loss, GeneratedAt = at,
        Recommendation = "Stored recommendation", ModelVersion = $"model-{id}", InputSnapshotJson = json };

    [Theory]
    [InlineData("PENDING_QC", 100)]
    [InlineData("DISPOSED", 100)]
    [InlineData("IN_STOCK", 0)]
    public async Task NoEligibleStockDoesNotCallModelOrCreateForecast(string status, int stock)
    {
        await using var db = Database(); var batch = (await SeedAsync(db))[0];
        batch.BatchStatus = status; batch.Inventories.Single().QuantityOnHand = stock; await db.SaveChangesAsync();
        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        var (forecasts, error) = await Service(db, model).GenerateForecastsAsync();
        Assert.Empty(forecasts); Assert.Null(error); Assert.Empty(await db.AiRiskForecasts.ToListAsync()); model.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GenerationGroupsSixBatchesAndPreservesFullRecommendationAndStructuredActions()
    {
        await using var db = Database(); var batches = await SeedAsync(db, 7); var full = new string('x', 1600);
        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        model.SetupSequence(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WarehouseForecastSqlTests.ModelResponse(batches.Take(6), full), "new-model"))
            .ReturnsAsync((WarehouseForecastSqlTests.ModelResponse(batches.Skip(6), full), "new-model"));
        var service = Service(db, model); var (generated, error) = await service.GenerateForecastsAsync();
        Assert.Null(error); Assert.Equal(batches.Select(b => b.ProductBatchId), generated.Select(f => f.ProductBatchId));
        Assert.All(generated, f => { Assert.False(f.IsFromCache); Assert.Equal(TimeSpan.FromHours(7), f.GeneratedAt.Offset); Assert.Equal(full, f.Recommendation); });
        var saved = await db.AiRiskForecasts.AsNoTracking().ToListAsync();
        Assert.Equal(7, saved.Count); Assert.All(saved, f => { Assert.Equal(1500, f.Recommendation!.Length); Assert.Null(f.SpoilageRiskScore); Assert.Equal(12.5m, f.PredictedLossPercent); });
        model.Verify(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        var reread = (await service.GetLatestForecastAsync(1))!;
        Assert.True(reread.IsFromCache); Assert.Equal(full, reread.Recommendation);
        Assert.Equal("Kiểm tra nhiệt độ", Assert.Single(reread.Actions).Action); Assert.Equal("Đối chiếu nhật ký kho", reread.AnalysisNotes);
    }

    [Fact]
    public async Task InvalidLaterModelGroupDoesNotPersistAnyEarlierGroup()
    {
        await using var db = Database(); var batches = await SeedAsync(db, 7);
        var previous = Forecast(batches[0], 50, "HIGH", 50, DateTime.UtcNow.AddMinutes(-1)); db.Add(previous); await db.SaveChangesAsync();
        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        model.SetupSequence(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WarehouseForecastSqlTests.ModelResponse(batches.Take(6)), "new-model"))
            .ReturnsAsync(("{\"forecasts\":[]}", "new-model"));
        var (forecasts, error) = await Service(db, model).GenerateForecastsAsync();
        Assert.Empty(forecasts); Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(50, Assert.Single(await db.AiRiskForecasts.ToListAsync()).AiRiskForecastId);
        Assert.DoesNotContain(db.ChangeTracker.Entries<AiRiskForecast>(), entry => entry.State == EntityState.Added);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("json")]
    public async Task ProviderFailureReturnsUserFacingErrorWithoutWriting(string failure)
    {
        await using var db = Database(); await SeedAsync(db);
        Exception exception = failure switch { "http" => new HttpRequestException("private provider details"), "timeout" => new OperationCanceledException(), _ => new JsonException("private provider details") };
        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        model.Setup(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(exception);
        var (forecasts, error) = await Service(db, model).GenerateForecastsAsync();
        Assert.Empty(forecasts); Assert.False(string.IsNullOrWhiteSpace(error)); Assert.DoesNotContain("private provider details", error);
        Assert.Empty(await db.AiRiskForecasts.ToListAsync());
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfBecomingProviderTimeoutMessage()
    {
        await using var db = Database(); await SeedAsync(db); using var cancellation = new CancellationTokenSource();
        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        model.Setup(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), cancellation.Token))
            .Returns(() => { cancellation.Cancel(); return Task.FromCanceled<(string, string)>(cancellation.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db, model).GenerateForecastsAsync(cancellation.Token));
        Assert.Empty(await db.AiRiskForecasts.ToListAsync());
    }

    [Fact]
    public async Task LatestAndHighRiskListsIgnoreStaleHighRiskAndStockNoLongerEligible()
    {
        await using var db = Database(); var batches = await SeedAsync(db, 5); var at = new DateTime(2026, 11, 1, 5, 0, 0);
        db.AddRange(Forecast(batches[0], 1, "CRITICAL", 99, at.AddMinutes(-1)), Forecast(batches[0], 2, "LOW", 1, at),
            Forecast(batches[1], 3, "HIGH", 80, at), Forecast(batches[2], 4, "CRITICAL", 40, at),
            Forecast(batches[3], 5, "CRITICAL", 100, at), Forecast(batches[4], 6, "CRITICAL", 100, at));
        batches[3].Inventories.Single().QuantityOnHand = 0; batches[4].BatchStatus = "DISPOSED"; await db.SaveChangesAsync();
        var service = Service(db, new Mock<IForecastModelClient>(MockBehavior.Strict));
        Assert.Equal(new long[] { 1, 2, 3 }, (await service.GetLatestForecastsAsync()).Select(f => f.ProductBatchId));
        Assert.Equal(new long[] { 3, 2 }, (await service.GetHighRiskBatchesAsync()).Select(f => f.ProductBatchId));
        Assert.Equal(3, Assert.Single(await service.GetHighRiskBatchesAsync(1)).ProductBatchId);
        var latest = (await service.GetLatestForecastAsync(1))!; Assert.Equal("model-2", latest.ModelVersion);
        Assert.Equal(at.AddHours(7), latest.GeneratedAt.DateTime); Assert.Equal(TimeSpan.FromHours(7), latest.GeneratedAt.Offset);
        // Direct historical lookup remains available after stock leaves eligibility.
        Assert.NotNull(await service.GetLatestForecastAsync(4)); Assert.Null(await service.GetLatestForecastAsync(999));
    }

    [Fact]
    public async Task HistoryBreaksTimestampTiesByIdAndCapsResultsAtTwenty()
    {
        await using var db = Database(); var batch = (await SeedAsync(db))[0]; var at = DateTime.UtcNow;
        db.AddRange(Enumerable.Range(1, 25).Select(id => Forecast(batch, id, "LOW", 1, at))); await db.SaveChangesAsync();
        var service = Service(db, new Mock<IForecastModelClient>(MockBehavior.Strict));
        Assert.Equal("model-25", (await service.GetLatestForecastAsync(1))!.ModelVersion);
        var history = await service.GetForecastHistoryAsync(1, 100);
        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(id => $"model-{id}"), history.Select(f => f.ModelVersion));
        Assert.All(history, f => Assert.False(f.IsFromCache)); Assert.Equal(5, (await service.GetForecastHistoryAsync(1)).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid json")]
    [InlineData("{}")]
    public async Task LegacyOrMalformedSnapshotStillReturnsStoredRecommendation(string? json)
    {
        await using var db = Database(); var batch = (await SeedAsync(db))[0]; db.Add(Forecast(batch, 1, "LOW", 1, DateTime.UtcNow, json)); await db.SaveChangesAsync();
        var result = (await Service(db, new Mock<IForecastModelClient>(MockBehavior.Strict)).GetLatestForecastAsync(1))!;
        Assert.Equal("Stored recommendation", result.Recommendation); Assert.Empty(result.Actions); Assert.Null(result.AnalysisNotes);
    }

    [Fact]
    public async Task CollectorUsesLatestCompletedQcRecentEnvironmentAndLargestPositiveStorageLocation()
    {
        await using var db = Database(); var batch = (await SeedAsync(db))[0]; batch.ExpectedMinTempC = 5; batch.ExpectedMaxTempC = 10; batch.ShelfLifeDaysSnapshot = 20;
        var now = DateTime.UtcNow;
        db.AddRange(new QcInspection { ProductBatch = batch, QcInspectionId = 1, InspectionCode = "QC-1", InspectionStatus = "COMPLETED", CompletedAt = now.AddDays(-1) },
            new QcInspection { ProductBatch = batch, QcInspectionId = 2, InspectionCode = "QC-2", InspectionStatus = "COMPLETED", CompletedAt = now },
            new QcInspection { ProductBatch = batch, QcInspectionId = 3, InspectionCode = "QC-3", InspectionStatus = "DRAFT", StartedAt = now.AddMinutes(1) });
        db.AddRange(new EnvironmentLog { ProductBatch = batch, RecordedAt = now.AddHours(-1), TemperatureC = 4, HumidityPct = 60 },
            new EnvironmentLog { ProductBatch = batch, RecordedAt = now.AddHours(-2), TemperatureC = 10 },
            new EnvironmentLog { ProductBatch = batch, RecordedAt = now.AddHours(-73), TemperatureC = 99, HumidityPct = 99 });
        db.Add(new Inventory { ProductBatch = batch, QuantityOnHand = 150, WarehouseLocation = new WarehouseLocation { LocationCode = "B1", ZoneName = "Larger" } });
        db.Add(new Inventory { ProductBatch = batch, QuantityOnHand = 0, WarehouseLocation = new WarehouseLocation { LocationCode = "C1", ZoneName = "Empty" } });
        await db.SaveChangesAsync();
        var snapshot = await new ForecastInputCollector(db).CollectAsync(batch, default);
        Assert.Equal("QC-2", snapshot.QcInspection!.InspectionCode); Assert.Equal(250, snapshot.QuantityOnHandKg); Assert.Equal("B1", snapshot.CurrentLocation!.LocationCode);
        Assert.Equal(3, snapshot.DaysFromHarvest); Assert.Equal(7, snapshot.DaysUntilExpiry); Assert.Equal(15m, snapshot.ShelfLifeConsumedPct);
        Assert.Equal(2, snapshot.EnvStats!.LogCount); Assert.Equal(7m, snapshot.EnvStats.AvgTempC); Assert.Equal(60m, snapshot.EnvStats.AvgHumidityPct);
        Assert.Equal(1, snapshot.EnvStats.TempDeviationCount); Assert.Equal(2, snapshot.RecentEnvLogs.Count);
    }
}
