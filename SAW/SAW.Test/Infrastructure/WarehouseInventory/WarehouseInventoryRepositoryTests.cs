using Microsoft.EntityFrameworkCore;
using Moq;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.WarehouseInventory;

// Verifies read aggregation with the real repository. SQL constraints/transactions remain SQL-test concerns.
public sealed class WarehouseInventoryRepositoryTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static WarehouseInventoryRepository Repository(AppDbContext db) => new(db, Mock.Of<IOrderReservationAllocator>(MockBehavior.Strict));
    private static WarehouseLocation Location(string code, decimal? capacity, string status = "ACTIVE") => new()
        { LocationCode = code, ZoneName = "Test", MaxWeightKg = capacity, LocationStatus = status };
    private static ProductBatch Batch(string code, string? grade) => new()
    {
        BatchCode = code, ProductName = code, Unit = "Kg", QualityGrade = grade, BatchStatus = "IN_STOCK",
        CropType = new CropType { CropCode = code, CropName = code, CategoryName = "Fruit", SafetyStockLevelKg = 10, IsActive = true }
    };
    private static Inventory Stock(WarehouseLocation location, ProductBatch batch, decimal amount, decimal reserved = 0) => new()
    {
        WarehouseLocation = location, ProductBatch = batch, QuantityOnHand = amount,
        ReservedQuantity = reserved, AvailableQuantity = amount - reserved, LastUpdatedAt = new(2026, 11, 1)
    };

    [Fact]
    public async Task EmptyWarehouse_ProducesZeroTotalsAndNoInventedQualityGrade()
    {
        await using var db = Database(); var repository = Repository(db);
        var levels = await repository.GetLevelChartAsync(default);
        var capacity = await repository.GetCapacityChartAsync(default);
        var quality = await repository.GetQualityDistributionChartAsync(default);
        Assert.Equal(0, levels.TotalOnHandKg); Assert.Empty(levels.Locations);
        Assert.Equal(0, capacity.UtilizationPercent); Assert.Equal(0, capacity.AvailableWeightKg);
        Assert.Equal(0, quality.TotalBatchCount); Assert.Null(quality.LeadingGrade); Assert.Empty(quality.Grades);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task InventoryLevels_SeparateOnHandReservedAvailableAndThresholdCountsAcrossLocations()
    {
        await using var db = Database();
        var first = Location("B", 100); var second = Location("A", null); var inactive = Location("C", 100, "INACTIVE");
        db.AddRange(Stock(first, Batch("B-1", "A"), 70, 20), Stock(first, Batch("B-2", "B"), 15, 10),
            Stock(second, Batch("B-3", "E"), 10), inactive);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = await Repository(db).GetLevelChartAsync(default);
        Assert.Equal(95, result.TotalOnHandKg); Assert.Equal(30, result.TotalReservedKg); Assert.Equal(65, result.TotalAvailableKg);
        Assert.Equal(2, result.ActiveLocationCount); Assert.Equal(2, result.LowStockItemCount);
        Assert.Equal(new[] { "A", "B", "C" }, result.Locations.Select(l => l.LocationCode));
        Assert.Equal(85, result.Locations[1].UtilizationPercent); Assert.Null(result.Locations[0].UtilizationPercent);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(89, "AVAILABLE")]
    [InlineData(90, "NEAR_CAPACITY")]
    [InlineData(100, "NEAR_CAPACITY")]
    [InlineData(101, "OVERCROWDED")]
    public async Task Capacity_UsesStoredWeightAndCurrentThresholdsWithoutNegativeFreeCapacity(int used, string status)
    {
        await using var db = Database(); db.Add(Stock(Location("A", 100), Batch("B-1", "A"), used, 20));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = await Repository(db).GetCapacityChartAsync(default);
        Assert.Equal(used, result.UsedWeightKg); Assert.Equal(used, result.UtilizationPercent);
        Assert.Equal(Math.Max(100 - used, 0), result.AvailableWeightKg);
        Assert.Equal(status, Assert.Single(result.Locations).Status);
        Assert.Equal(status == "NEAR_CAPACITY" ? 1 : 0, result.NearCapacityCount);
        Assert.Equal(status == "OVERCROWDED" ? 1 : 0, result.OvercrowdedCount);
    }

    [Fact]
    public async Task Capacity_WithoutConfiguredLimit_DoesNotInventALimitOrDivideByZero()
    {
        await using var db = Database(); db.Add(Stock(Location("A", null), Batch("B-1", "A"), 50));
        await db.SaveChangesAsync();
        var result = await Repository(db).GetCapacityChartAsync(default);
        var item = Assert.Single(result.Locations);
        Assert.Equal("UNCONFIGURED", item.Status); Assert.Null(item.AvailableWeightKg); Assert.Null(item.UtilizationPercent);
        Assert.Equal(0, result.ConfiguredCapacityKg); Assert.Equal(0, result.UtilizationPercent);
    }

    [Fact]
    public async Task QualityDistribution_CountsDistinctLotsAcrossLocations_ExcludesZeroStock_AndKeepsUngradedSeparate()
    {
        await using var db = Database();
        var first = Location("A", 100); var second = Location("B", 100);
        var shared = Batch("A-1", "A");
        db.AddRange(Stock(first, shared, 20), Stock(second, shared, 30), Stock(first, Batch("E-1", "E"), 30),
            Stock(first, Batch("U-1", null), 20), Stock(second, Batch("EMPTY", "C"), 0));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = await Repository(db).GetQualityDistributionChartAsync(default);
        Assert.Equal(3, result.TotalBatchCount); Assert.Equal(2, result.GradedBatchCount); Assert.Equal(1, result.UngradedBatchCount);
        Assert.Equal(100, result.TotalQuantityOnHandKg); Assert.Equal("A", result.LeadingGrade);
        var gradeA = Assert.Single(result.Grades, g => g.Grade == "A");
        Assert.Equal(1, gradeA.BatchCount); Assert.Equal(50, gradeA.QuantityOnHandKg); Assert.Equal(50, gradeA.Percentage);
        Assert.Equal(30, Assert.Single(result.Grades, g => g.Grade == "E").Percentage);
        Assert.Equal(20, Assert.Single(result.Grades, g => g.Grade == "UNGRADED").Percentage);
        Assert.DoesNotContain(result.Grades, g => g.Grade == "C"); Assert.Empty(db.ChangeTracker.Entries());
    }
}
