using FluentAssertions;
using Moq;
using SAW.Application.Features.QcInspections;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using Xunit;

namespace SAW.Test.Application.QcInspections;

public sealed class StorageEnvironmentMonitoringTests
{
    private readonly Mock<IQcInspectionRepository> _repo = new();

    private QcInspectionService CreateService() => new(_repo.Object);

    [Fact]
    public async Task GetInStorageBatchesWithEnvAsync_DetectsOutOfRangeAndHoursSinceLog()
    {
        // Arrange
        var batch = new ProductBatch
        {
            ProductBatchId = 101,
            BatchCode = "LH-IN-STORAGE-01",
            ProductName = "Dưa lưới",
            QualityGrade = "A",
            WeightInKg = 1200m,
            BatchStatus = "IN_STOCK",
            ExpectedMinTempC = 5m,
            ExpectedMaxTempC = 10m,
            ExpectedMinHumidityPct = 80m,
            ExpectedMaxHumidityPct = 90m,
            Inventories = new List<Inventory>
            {
                new()
                {
                    QuantityOnHand = 1200m,
                    WarehouseLocation = new WarehouseLocation
                    {
                        WarehouseLocationId = 12,
                        LocationCode = "KHO-L1-B05",
                        ZoneName = "Kho Mát L1"
                    }
                }
            },
            EnvironmentLogs = new List<EnvironmentLog>
            {
                new()
                {
                    EnvironmentLogId = 1,
                    TemperatureC = 12.5m, // Ngoài ngưỡng (5 - 10)
                    HumidityPct = 85m,
                    RecordedAt = DateTime.UtcNow.AddHours(-3),
                    RecordedByAccount = new Account { FullName = "Nguyễn Văn QC" }
                }
            }
        };

        _repo.Setup(r => r.ListInStorageBatchesAsync(null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([batch], 1));

        var service = CreateService();

        // Act
        var result = await service.GetInStorageBatchesWithEnvAsync(new InStorageBatchEnvQuery(), CancellationToken.None);

        // Assert
        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);

        var item = result.Items[0];
        item.BatchCode.Should().Be("LH-IN-STORAGE-01");
        item.LocationCode.Should().Be("KHO-L1-B05");
        item.ZoneName.Should().Be("Kho Mát L1");
        item.LastTemperatureC.Should().Be(12.5m);
        item.IsLastOutOfRange.Should().BeTrue("Nhiệt độ 12.5 vượt quá Max 10.0 nên phải báo lệch");
        item.HoursSinceLastLog.Should().BeApproximately(3.0, 0.2);
        item.TotalLogsCount.Should().Be(1);
    }

    [Fact]
    public async Task GetBatchEnvironmentLogsAsync_ReturnsAllLogsAndFlagsOutOfRange()
    {
        // Arrange
        const long batchId = 202;
        var batch = new ProductBatch
        {
            ProductBatchId = batchId,
            ExpectedMinTempC = 2m,
            ExpectedMaxTempC = 6m
        };

        var logs = new List<EnvironmentLog>
        {
            new()
            {
                EnvironmentLogId = 11,
                ProductBatchId = batchId,
                TemperatureC = 4.0m, // Trong ngưỡng
                HumidityPct = 88m,
                SourceType = "MANUAL",
                RecordedAt = DateTime.UtcNow.AddHours(-1)
            },
            new()
            {
                EnvironmentLogId = 12,
                ProductBatchId = batchId,
                TemperatureC = 8.5m, // Vượt ngưỡng
                HumidityPct = 92m,
                SourceType = "MANUAL",
                RecordedAt = DateTime.UtcNow.AddHours(-5)
            }
        };

        _repo.Setup(r => r.GetBatchAsync(batchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);

        _repo.Setup(r => r.GetBatchEnvironmentLogsAsync(batchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(logs);

        var service = CreateService();

        // Act
        var result = await service.GetBatchEnvironmentLogsAsync(batchId, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result[0].IsTempOutOfRange.Should().BeFalse();
        result[1].IsTempOutOfRange.Should().BeTrue();
    }

    [Fact]
    public async Task CreateEnvironmentLogAsync_WhenLocationIsNull_AutoResolvesCurrentBatchLocation()
    {
        // Arrange
        const long batchId = 303;
        var batch = new ProductBatch
        {
            ProductBatchId = batchId,
            ExpectedMinTempC = 0m,
            ExpectedMaxTempC = 5m
        };

        var currentLocation = new WarehouseLocation
        {
            WarehouseLocationId = 77,
            LocationCode = "LOC-AUTO-77"
        };

        _repo.Setup(r => r.GetBatchAsync(batchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);

        _repo.Setup(r => r.GetBatchCurrentLocationAsync(batchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentLocation);

        EnvironmentLog? capturedLog = null;
        _repo.Setup(r => r.AddEnvironmentLog(It.IsAny<EnvironmentLog>()))
            .Callback<EnvironmentLog>(l => capturedLog = l);

        var service = CreateService();

        var request = new CreateEnvironmentLogRequest(
            ProductBatchId: batchId,
            WarehouseLocationId: null, // Không truyền location
            QcInspectionId: null,
            TemperatureC: 3.5m,
            HumidityPct: 80m,
            SourceType: "MANUAL",
            SensorIdentifier: null,
            RecordedAt: DateTime.UtcNow,
            Note: "Kiểm tra định kỳ"
        );

        // Act
        var result = await service.CreateEnvironmentLogAsync(request, actorAccountId: 99, CancellationToken.None);

        // Assert
        capturedLog.Should().NotBeNull();
        capturedLog!.WarehouseLocationId.Should().Be(77, "Hệ thống phải tự động gán vị trí hiện tại của lô hàng");
        result.TemperatureC.Should().Be(3.5m);
        result.IsTempOutOfRange.Should().BeFalse();
    }

    [Fact]
    public async Task CreateEnvironmentLogAsync_WhenRecordedAtIsVietnamLocalTime_NormalizesToUtcSuccessfully()
    {
        // Arrange: Giả lập client gửi giờ địa phương Việt Nam (Kind = Unspecified, ví dụ 21:50)
        const long batchId = 404;
        var batch = new ProductBatch
        {
            ProductBatchId = batchId,
            ExpectedMinTempC = 0m,
            ExpectedMaxTempC = 10m
        };

        _repo.Setup(r => r.GetBatchAsync(batchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);

        EnvironmentLog? capturedLog = null;
        _repo.Setup(r => r.AddEnvironmentLog(It.IsAny<EnvironmentLog>()))
            .Callback<EnvironmentLog>(l => capturedLog = l);

        var service = CreateService();

        // Thời gian Việt Nam hiện tại: UTC + 7 tiếng
        var vnLocalNow = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(7), DateTimeKind.Unspecified);

        var request = new CreateEnvironmentLogRequest(
            ProductBatchId: batchId,
            WarehouseLocationId: null,
            QcInspectionId: null,
            TemperatureC: 5.0m,
            HumidityPct: 75m,
            SourceType: "MANUAL",
            SensorIdentifier: null,
            RecordedAt: vnLocalNow, // Giờ VN (Unspecified)
            Note: "Đo tại chỗ"
        );

        // Act
        var result = await service.CreateEnvironmentLogAsync(request, actorAccountId: 10, CancellationToken.None);

        // Assert: Không được quăng exception và đã quy đổi chuẩn về UTC
        result.Should().NotBeNull();
        capturedLog.Should().NotBeNull();
        capturedLog!.RecordedAt.Kind.Should().Be(DateTimeKind.Utc);
        capturedLog.RecordedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateEnvironmentLogAsync_WhenRecordedAtIsFutureInVietnam_ThrowsBadRequestException()
    {
        // Arrange: Thời gian tương lai ở Việt Nam (lớn hơn hiện tại 1 tiếng)
        const long batchId = 505;
        var batch = new ProductBatch { ProductBatchId = batchId };
        _repo.Setup(r => r.GetBatchAsync(batchId, It.IsAny<CancellationToken>())).ReturnsAsync(batch);

        var service = CreateService();

        // Tương lai ở VN (UTC+7 + 1 tiếng)
        var futureVnTime = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(8), DateTimeKind.Unspecified);

        var request = new CreateEnvironmentLogRequest(
            ProductBatchId: batchId,
            WarehouseLocationId: 10,
            QcInspectionId: null,
            TemperatureC: 5.0m,
            HumidityPct: 75m,
            SourceType: "MANUAL",
            SensorIdentifier: null,
            RecordedAt: futureVnTime,
            Note: null
        );

        // Act
        var act = () => service.CreateEnvironmentLogAsync(request, actorAccountId: 10, CancellationToken.None);

        // Assert
        var ex = await Assert.ThrowsAsync<SAW.Application.Exceptions.BadRequestException>(act);
        ex.Message.Should().Contain("không được là thời gian trong tương lai");
        ex.Message.Should().Contain("Việt Nam");
    }

    [Fact]
    public async Task CreateEnvironmentLogAsync_WhenBatchNotFound_ThrowsNotFoundException()
    {
        // Arrange
        const long missingBatchId = 99999;
        _repo.Setup(r => r.GetBatchAsync(missingBatchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProductBatch?)null);

        var service = CreateService();

        var request = new CreateEnvironmentLogRequest(
            ProductBatchId: missingBatchId,
            WarehouseLocationId: null,
            QcInspectionId: null,
            TemperatureC: 5.0m,
            HumidityPct: 70m,
            SourceType: "MANUAL",
            SensorIdentifier: null,
            RecordedAt: DateTime.UtcNow,
            Note: null
        );

        // Act
        var act = () => service.CreateEnvironmentLogAsync(request, actorAccountId: 1, CancellationToken.None);

        // Assert
        var ex = await Assert.ThrowsAsync<SAW.Application.Exceptions.NotFoundException>(act);
        ex.Message.Should().Contain("Không tìm thấy lô hàng");
    }

    [Fact]
    public async Task GetInStorageBatchesWithEnvAsync_WhenFilteringByIsOutOfRange_ReturnsOnlyOutOfRangeBatches()
    {
        // Arrange
        var normalBatch = new ProductBatch
        {
            ProductBatchId = 1,
            BatchCode = "LH-NORMAL",
            ExpectedMinTempC = 5m,
            ExpectedMaxTempC = 15m,
            EnvironmentLogs = new List<EnvironmentLog>
            {
                new() { TemperatureC = 10m, RecordedAt = DateTime.UtcNow }
            }
        };

        var outOfRangeBatch = new ProductBatch
        {
            ProductBatchId = 2,
            BatchCode = "LH-OUT-OF-RANGE",
            ExpectedMinTempC = 5m,
            ExpectedMaxTempC = 15m,
            EnvironmentLogs = new List<EnvironmentLog>
            {
                new() { TemperatureC = 20m, RecordedAt = DateTime.UtcNow }
            }
        };

        _repo.Setup(r => r.ListInStorageBatchesAsync(null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([normalBatch, outOfRangeBatch], 2));

        var service = CreateService();

        // Act
        var result = await service.GetInStorageBatchesWithEnvAsync(
            new InStorageBatchEnvQuery(IsOutOfRange: true),
            CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].BatchCode.Should().Be("LH-OUT-OF-RANGE");
    }

    [Fact]
    public async Task GetInStorageBatchesWithEnvAsync_WhenFilteringByIsOverdue_ReturnsOnlyBatchesOver24HoursOrNoLogs()
    {
        // Arrange
        var recentBatch = new ProductBatch
        {
            ProductBatchId = 1,
            BatchCode = "LH-RECENT",
            EnvironmentLogs = new List<EnvironmentLog>
            {
                new() { TemperatureC = 10m, RecordedAt = DateTime.UtcNow.AddHours(-2) }
            }
        };

        var overdueBatch = new ProductBatch
        {
            ProductBatchId = 2,
            BatchCode = "LH-OVERDUE",
            EnvironmentLogs = new List<EnvironmentLog>
            {
                new() { TemperatureC = 10m, RecordedAt = DateTime.UtcNow.AddHours(-30) }
            }
        };

        var noLogBatch = new ProductBatch
        {
            ProductBatchId = 3,
            BatchCode = "LH-NOLOG",
            EnvironmentLogs = new List<EnvironmentLog>()
        };

        _repo.Setup(r => r.ListInStorageBatchesAsync(null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([recentBatch, overdueBatch, noLogBatch], 3));

        var service = CreateService();

        // Act
        var result = await service.GetInStorageBatchesWithEnvAsync(
            new InStorageBatchEnvQuery(IsOverdue: true),
            CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items.Select(x => x.BatchCode).Should().Contain(["LH-OVERDUE", "LH-NOLOG"]);
    }
}

