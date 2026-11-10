using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiForecast;
using SAW.Infrastructure.Persistence;
using SAW.Test.Infrastructure.ProductBatches;
using Xunit;

namespace SAW.Test.Infrastructure.AiForecast;

public sealed class WarehouseForecastSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task GenerateForecasts_CoversAllEligibleBatchesAndReadsOnlyLatestResults()
    {
        var first = await fixture.BatchAsync();
        var second = await fixture.BatchAsync();
        var empty = await fixture.BatchAsync();
        var pending = await fixture.BatchAsync();
        await using var db = fixture.Context();
        var recommendation = new string('x', 1600);
        await StockAsync(db, first, "IN_STOCK");
        await StockAsync(db, second, "PARTIALLY_ISSUED");
        await StockAsync(db, empty, "IN_STOCK", quantity: 0);

        db.AiRiskForecasts.AddRange(new[] { first, empty, pending }.Select(batch => new AiRiskForecast
        {
            ProductBatchId = batch.ProductBatchId,
            SpoilageRiskScore = 95m,
            RiskLevel = "CRITICAL",
            GeneratedAt = DateTime.UtcNow.AddMinutes(-1),
            ModelVersion = "old-model"
        }));
        await db.SaveChangesAsync();

        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        model.Setup(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, prompt, _) =>
            {
                prompt.Should().Contain(first.BatchCode).And.Contain(second.BatchCode);
                prompt.Should().NotContain(empty.BatchCode).And.NotContain(pending.BatchCode);
            })
            .ReturnsAsync((ModelResponse([second, first], recommendation), "test-model"));

        var service = new SpoilageForecastService(db, model.Object, new ForecastInputCollector(db),
            NullLogger<SpoilageForecastService>.Instance);
        var (forecasts, error) = await service.GenerateForecastsAsync();

        error.Should().BeNull();
        forecasts.Select(f => f.ProductBatchId).Should().Equal(first.ProductBatchId, second.ProductBatchId);
        model.Verify(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        await using var readDb = fixture.Context();
        var saved = await readDb.AiRiskForecasts.Where(f => f.ModelVersion == "test-model")
            .OrderBy(f => f.ProductBatchId).ToListAsync();
        saved.Select(f => f.ProductBatchId).Should().Equal(first.ProductBatchId, second.ProductBatchId);
        saved.Should().OnlyContain(f => f.SpoilageRiskScore == null && f.Recommendation!.Length == 1500
            && f.PredictedLossPercent == 12.5m && f.SafeStorageRemainingDays == 3);
        forecasts.Should().OnlyContain(f => !f.IsFromCache && f.GeneratedAt.Offset == TimeSpan.FromHours(7));
        (await service.GetLatestForecastsAsync()).Should().BeEquivalentTo(
            forecasts.Select(f => f with { IsFromCache = true }), options => options.WithStrictOrdering());
        var latest = (await service.GetLatestForecastAsync(first.ProductBatchId))!;
        latest.Recommendation.Should().Be(recommendation);
        latest.Actions.Should().ContainSingle().Which.Action.Should().Be("Kiểm tra nhiệt độ");
        (await service.GetForecastHistoryAsync(first.ProductBatchId)).Select(f => f.ModelVersion)
            .Should().Equal("test-model", "old-model");
        (await service.GetHighRiskBatchesAsync()).Should().BeEmpty();

        await db.Inventories.Where(i => i.ProductBatchId == second.ProductBatchId)
            .ExecuteUpdateAsync(update => update.SetProperty(i => i.QuantityOnHand, 0m));
        (await service.GetLatestForecastsAsync()).Should().ContainSingle()
            .Which.ProductBatchId.Should().Be(first.ProductBatchId);
    }

    internal static string ModelResponse(IEnumerable<ProductBatch> batches, string recommendation = "Kiểm tra kho") =>
        JsonSerializer.Serialize(new
        {
            forecasts = batches.Select(b => new
            {
                productBatchId = b.ProductBatchId,
                riskLevel = "LOW",
                predictedLossPercent = 12.5m,
                safeStorageRemainingDays = 3,
                forecastHorizonDays = 7,
                recommendation,
                actions = new[] { new { priority = "URGENT", category = "TEMPERATURE",
                    action = "Kiểm tra nhiệt độ", reason = "Nhiệt độ dao động", deadline = (string?)null } },
                analysisNotes = "Đối chiếu nhật ký kho"
            })
        });

    internal static async Task StockAsync(AppDbContext db, ProductBatch batch, string status, decimal quantity = 100)
    {
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId)
            .ExecuteUpdateAsync(update => update.SetProperty(b => b.BatchStatus, status));
        var location = new WarehouseLocation
        {
            LocationCode = "FC-" + Guid.NewGuid().ToString("N")[..12],
            ZoneName = "Forecast test"
        };
        db.WarehouseLocations.Add(location);
        await db.SaveChangesAsync();

        // The fixture creates AvailableQuantity as a regular column.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO INVENTORY (ProductBatchID, WarehouseLocationID, QuantityOnHand, ReservedQuantity, AvailableQuantity, Unit)
            VALUES ({batch.ProductBatchId}, {location.WarehouseLocationId}, {quantity}, 0, {quantity}, 'kg')
            """);
    }
}

public sealed class WarehouseForecastEligibilitySqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task GenerateForecasts_WithoutEligibleBatches_DoesNotCallModelOrWrite()
    {
        var pending = await fixture.BatchAsync();
        await using var db = fixture.Context();
        await WarehouseForecastSqlTests.StockAsync(db, pending, "PENDING_QC");
        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        var service = new SpoilageForecastService(db, model.Object, new ForecastInputCollector(db),
            NullLogger<SpoilageForecastService>.Instance);

        var (forecasts, error) = await service.GenerateForecastsAsync();

        error.Should().BeNull();
        forecasts.Should().BeEmpty();
        model.VerifyNoOtherCalls();
        (await db.AiRiskForecasts.AnyAsync(f => f.ProductBatchId == pending.ProductBatchId)).Should().BeFalse();
    }
}

public sealed class WarehouseForecastAtomicitySqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task GenerateForecasts_IncompleteLaterGroupDoesNotSavePartialResults()
    {
        await using var db = fixture.Context();
        var batches = new List<ProductBatch>();
        for (var index = 0; index < 7; index++)
        {
            var batch = await fixture.BatchAsync();
            await WarehouseForecastSqlTests.StockAsync(db, batch, "IN_STOCK");
            batches.Add(batch);
        }

        var previous = new AiRiskForecast
        {
            ProductBatchId = batches[0].ProductBatchId, RiskLevel = "HIGH", ModelVersion = "previous-model"
        };
        db.AiRiskForecasts.Add(previous);
        await db.SaveChangesAsync();

        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        model.SetupSequence(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WarehouseForecastSqlTests.ModelResponse(batches.Take(6)), "test-model"))
            .ReturnsAsync((WarehouseForecastSqlTests.ModelResponse([]), "test-model"));

        var service = new SpoilageForecastService(db, model.Object, new ForecastInputCollector(db),
            NullLogger<SpoilageForecastService>.Instance);
        var (forecasts, error) = await service.GenerateForecastsAsync();

        forecasts.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
        model.Verify(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        await using var readDb = fixture.Context();
        (await readDb.AiRiskForecasts.Select(f => f.AiRiskForecastId).ToListAsync())
            .Should().Equal(previous.AiRiskForecastId);
    }
}
