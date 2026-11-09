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
            .ReturnsAsync((ModelResponse([second, first]), "test-model"));

        var service = new SpoilageForecastService(db, model.Object, new ForecastInputCollector(db),
            NullLogger<SpoilageForecastService>.Instance);
        var (forecasts, error) = await service.GenerateForecastsAsync();

        error.Should().BeNull();
        forecasts.Select(f => f.ProductBatchId).Should().Equal(first.ProductBatchId, second.ProductBatchId);
        model.Verify(m => m.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        (await db.AiRiskForecasts.Where(f => f.ModelVersion == "test-model").ToListAsync())
            .Should().OnlyContain(f => f.SpoilageRiskScore == null);
        (await service.GetLatestForecastsAsync()).Should().Equal(forecasts,
            (saved, generated) => saved.ProductBatchId == generated.ProductBatchId && saved.ModelVersion == generated.ModelVersion);
        (await service.GetHighRiskBatchesAsync()).Should().BeEmpty();

        await db.Inventories.Where(i => i.ProductBatchId == second.ProductBatchId)
            .ExecuteUpdateAsync(update => update.SetProperty(i => i.QuantityOnHand, 0m));
        (await service.GetLatestForecastsAsync()).Should().ContainSingle()
            .Which.ProductBatchId.Should().Be(first.ProductBatchId);
    }

    internal static string ModelResponse(IEnumerable<ProductBatch> batches) =>
        JsonSerializer.Serialize(new
        {
            forecasts = batches.Select(b => new { productBatchId = b.ProductBatchId, riskLevel = "LOW" })
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
        (await db.AiRiskForecasts.AnyAsync(f => f.ModelVersion == "test-model")).Should().BeFalse();
    }
}
