using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.QcInspections;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.QcInspections;

public sealed class StorageEnvironmentBoundaryTests
{
    private readonly Mock<IQcInspectionRepository> _repo = new();
    private QcInspectionService Service() => new(_repo.Object);
    private static ProductBatch Batch() => new()
    {
        ProductBatchId = 1, BatchCode = "ENV-1", ProductName = "Mango", BatchStatus = "IN_STOCK",
        ExpectedMinTempC = 2, ExpectedMaxTempC = 6,
        ExpectedMinHumidityPct = 80, ExpectedMaxHumidityPct = 90
    };

    private static CreateEnvironmentLogRequest Request() =>
        new(1, null, null, 4, 85, "MANUAL", null, DateTime.UtcNow, null);

    private void SetupBatch(ProductBatch batch) =>
        _repo.Setup(r => r.GetBatchAsync(batch.ProductBatchId, It.IsAny<CancellationToken>())).ReturnsAsync(batch);

    private void SetupList(int total, params ProductBatch[] batches) =>
        _repo.Setup(r => r.ListInStorageBatchesAsync(null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<ProductBatch>)batches, total));

    private void SetupHistory(ProductBatch batch, params EnvironmentLog[] logs)
    {
        SetupBatch(batch);
        _repo.Setup(r => r.GetBatchEnvironmentLogsAsync(batch.ProductBatchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(logs);
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("100.001")]
    public async Task CreateRejectsHumidityOutsideRangeBeforeRoundingOrWriting(string humidity)
    {
        var request = Request() with { HumidityPct = decimal.Parse(humidity, System.Globalization.CultureInfo.InvariantCulture) };

        await Assert.ThrowsAsync<BadRequestException>(() => Service().CreateEnvironmentLogAsync(request, 7, default));

        _repo.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task CreateAcceptsHumidityAtBothRangeBoundaries(int humidity)
    {
        SetupBatch(Batch());

        var result = await Service().CreateEnvironmentLogAsync(Request() with { HumidityPct = humidity }, 7, default);

        Assert.Equal((decimal)humidity, result.HumidityPct);
        _repo.Verify(r => r.AddEnvironmentLog(It.Is<EnvironmentLog>(log => log.HumidityPct == humidity)), Times.Once);
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateRejectsUnsupportedSourceBeforeRepositoryAccess()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service()
            .CreateEnvironmentLogAsync(Request() with { SourceType = "AUTOMATIC" }, 7, default));

        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreatePersistsOutOfRangeTemperatureAndSetsWarningFlag()
    {
        SetupBatch(Batch());

        var result = await Service().CreateEnvironmentLogAsync(Request() with { TemperatureC = 8.5m }, 7, default);

        Assert.True(result.IsTempOutOfRange);
        Assert.Equal(8.5m, result.TemperatureC);
        _repo.Verify(r => r.AddEnvironmentLog(It.Is<EnvironmentLog>(log => log.TemperatureC == 8.5m)), Times.Once);
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAcceptsOneMinuteClockDriftButRejectsThreeMinutes()
    {
        SetupBatch(Batch());
        var now = DateTime.UtcNow;

        var result = await Service().CreateEnvironmentLogAsync(Request() with { RecordedAt = now.AddMinutes(1) }, 7, default);
        Assert.Equal(now.AddMinutes(1), result.RecordedAt);
        await Assert.ThrowsAsync<BadRequestException>(() => Service()
            .CreateEnvironmentLogAsync(Request() with { RecordedAt = now.AddMinutes(3) }, 7, default));

        _repo.Verify(r => r.AddEnvironmentLog(It.IsAny<EnvironmentLog>()), Times.Once);
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateRejectsMissingOrInactiveExplicitLocationBeforeWrite()
    {
        _repo.Setup(r => r.WarehouseLocationExistsAsync(77, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<NotFoundException>(() => Service()
            .CreateEnvironmentLogAsync(Request() with { WarehouseLocationId = 77 }, 7, default));

        _repo.Verify(r => r.GetBatchAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.AddEnvironmentLog(It.IsAny<EnvironmentLog>()), Times.Never);
    }

    [Fact]
    public async Task CreateAllowsNullHumidityAndUnresolvedLocationAndTrimsText()
    {
        SetupBatch(Batch());
        var request = Request() with { HumidityPct = null, SensorIdentifier = " sensor-1 ", Note = " measured manually " };

        var result = await Service().CreateEnvironmentLogAsync(request, 7, default);

        Assert.Null(result.HumidityPct);
        Assert.Null(result.WarehouseLocationId);
        Assert.Equal("sensor-1", result.SensorIdentifier);
        Assert.Equal("measured manually", result.Note);
        _repo.Verify(r => r.AddEnvironmentLog(It.Is<EnvironmentLog>(log => log.HumidityPct == null
            && log.WarehouseLocationId == null && log.RecordedByAccountId == 7
            && log.SensorIdentifier == "sensor-1" && log.Note == "measured manually")), Times.Once);
    }

    [Fact]
    public async Task ListFlagsHumidityDeviationEvenWhenTemperatureIsNormal()
    {
        var batch = Batch();
        batch.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = 4, HumidityPct = 95, RecordedAt = DateTime.UtcNow });
        SetupList(1, batch);

        var result = Assert.Single((await Service().GetInStorageBatchesWithEnvAsync(new(), default)).Items);

        Assert.Equal(4m, result.LastTemperatureC);
        Assert.Equal(95m, result.LastHumidityPct);
        Assert.True(result.IsLastOutOfRange);
    }

    [Fact]
    public async Task ListUsesCropThresholdDefaultsAndPrefersExplicitBatchOverrides()
    {
        var batch = Batch();
        batch.ExpectedMinTempC = batch.ExpectedMaxTempC = batch.ExpectedMinHumidityPct = batch.ExpectedMaxHumidityPct = null;
        batch.CropType = new CropType { ExpectedMinTempC = 2, ExpectedMaxTempC = 6,
            ExpectedMinHumidityPct = 80, ExpectedMaxHumidityPct = 90 };
        batch.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = 8.5m, HumidityPct = 95, RecordedAt = DateTime.UtcNow });
        SetupList(1, batch);

        var fallback = Assert.Single((await Service().GetInStorageBatchesWithEnvAsync(new(), default)).Items);
        Assert.Equal(2m, fallback.ExpectedMinTempC);
        Assert.Equal(6m, fallback.ExpectedMaxTempC);
        Assert.Equal(80m, fallback.ExpectedMinHumidityPct);
        Assert.Equal(90m, fallback.ExpectedMaxHumidityPct);
        Assert.True(fallback.IsLastOutOfRange);

        batch.ExpectedMaxTempC = 10; batch.ExpectedMaxHumidityPct = 100;
        var overridden = Assert.Single((await Service().GetInStorageBatchesWithEnvAsync(new(), default)).Items);
        Assert.Equal(2m, overridden.ExpectedMinTempC);
        Assert.Equal(10m, overridden.ExpectedMaxTempC);
        Assert.Equal(100m, overridden.ExpectedMaxHumidityPct);
        Assert.False(overridden.IsLastOutOfRange);
    }

    [Fact]
    public async Task ListKeepsMeasurementsAndAgeNullForUnmeasuredBatches()
    {
        SetupList(1, Batch());

        var result = Assert.Single((await Service().GetInStorageBatchesWithEnvAsync(new(), default)).Items);

        Assert.Null(result.LastTemperatureC);
        Assert.Null(result.LastHumidityPct);
        Assert.Null(result.LastRecordedAt);
        Assert.Null(result.HoursSinceLastLog);
        Assert.Null(result.IsLastOutOfRange);
        Assert.Equal(0, result.TotalLogsCount);
    }

    [Fact]
    public async Task ListSelectsLocationWithLargestPositiveInventory()
    {
        var batch = Batch();
        foreach (var (id, quantity) in new[] { (1, 0), (2, 100), (3, 150) })
            batch.Inventories.Add(new Inventory { QuantityOnHand = quantity,
                WarehouseLocation = new WarehouseLocation { WarehouseLocationId = id, LocationCode = $"LOC-{id}", ZoneName = "Cold" } });
        SetupList(1, batch);

        var result = Assert.Single((await Service().GetInStorageBatchesWithEnvAsync(new(), default)).Items);

        Assert.Equal(3, result.WarehouseLocationId);
        Assert.Equal("LOC-3", result.LocationCode);
    }

    [Fact]
    public async Task ListTreatsRounded24HoursAsOverdue()
    {
        var now = DateTime.UtcNow;
        var overdue = Batch();
        overdue.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = 4, RecordedAt = now.AddHours(-23.98) });
        var recent = Batch(); recent.ProductBatchId = 2;
        recent.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = 4, RecordedAt = now.AddHours(-23.94) });
        SetupList(2, overdue, recent);

        var result = Assert.Single((await Service().GetInStorageBatchesWithEnvAsync(new(IsOverdue: true), default)).Items);

        Assert.Equal(1, result.ProductBatchId);
        Assert.Equal(24d, result.HoursSinceLastLog);
    }

    [Fact]
    public async Task ListPreservesRepositoryTotalAfterPostPaginationFiltering()
    {
        var normal = Batch();
        normal.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = 4, RecordedAt = DateTime.UtcNow });
        var first = Batch(); first.ProductBatchId = 2;
        first.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = 8, RecordedAt = DateTime.UtcNow });
        var second = Batch(); second.ProductBatchId = 3;
        second.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = 9, RecordedAt = DateTime.UtcNow });
        SetupList(20, normal, first, second);

        var result = await Service().GetInStorageBatchesWithEnvAsync(new(IsOutOfRange: true), default);

        Assert.Equal(20, result.TotalCount);
        Assert.Equal(new long[] { 2, 3 }, result.Items.Select(item => item.ProductBatchId));
    }

    [Theory]
    [InlineData(2, 80)]
    [InlineData(6, 90)]
    public async Task ListAcceptsMeasurementsExactlyAtStorageThresholds(int temperature, int humidity)
    {
        var batch = Batch();
        batch.EnvironmentLogs.Add(new EnvironmentLog { TemperatureC = temperature, HumidityPct = humidity, RecordedAt = DateTime.UtcNow });
        SetupList(1, batch);

        var result = Assert.Single((await Service().GetInStorageBatchesWithEnvAsync(new(), default)).Items);

        Assert.False(result.IsLastOutOfRange);
    }

    [Fact]
    public async Task HistoryRejectsMissingBatchBeforeLoadingLogs()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetBatchEnvironmentLogsAsync(999, default));

        _repo.Verify(r => r.GetBatchEnvironmentLogsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HistoryReturnsEmptyForExistingBatchWithoutLogs()
    {
        SetupHistory(Batch());

        Assert.Empty(await Service().GetBatchEnvironmentLogsAsync(1, default));
    }

    [Fact]
    public async Task HistoryFallsBackToCropTemperatureThresholds()
    {
        var batch = Batch(); batch.ExpectedMinTempC = batch.ExpectedMaxTempC = null;
        batch.CropType = new CropType { ExpectedMinTempC = 2, ExpectedMaxTempC = 6 };
        SetupHistory(batch, new EnvironmentLog { TemperatureC = 4 }, new EnvironmentLog { TemperatureC = 8.5m });

        var result = await Service().GetBatchEnvironmentLogsAsync(1, default);

        Assert.False(result[0].IsTempOutOfRange);
        Assert.True(result[1].IsTempOutOfRange);
    }

    [Fact]
    public async Task HistoryKeepsDeviationFlagNullWhenNoTemperatureThresholdExists()
    {
        var batch = Batch(); batch.ExpectedMinTempC = batch.ExpectedMaxTempC = null;
        SetupHistory(batch, new EnvironmentLog { TemperatureC = 100 });

        Assert.Null(Assert.Single(await Service().GetBatchEnvironmentLogsAsync(1, default)).IsTempOutOfRange);
    }

    [Fact]
    public async Task HistoryDoesNotTreatHumidityDeviationAsTemperatureDeviation()
    {
        SetupHistory(Batch(), new EnvironmentLog { TemperatureC = 4, HumidityPct = 95 });

        var result = Assert.Single(await Service().GetBatchEnvironmentLogsAsync(1, default));

        Assert.Equal(95m, result.HumidityPct);
        Assert.False(result.IsTempOutOfRange);
    }
}
