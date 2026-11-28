using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.AdminDashboard;

public sealed class AdminDashboardRepositoryTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static Account User(int id, string status, DateTime created, Role role) => new()
        { AccountId = id, Username = $"user{id}", Email = $"user{id}@test.invalid", FullName = $"User {id}", PasswordHash = "hash", AccountStatus = status, CreatedAt = created, Role = role };

    [Fact]
    public async Task EmptyDashboardReturnsSevenZeroFilledCalendarDays()
    {
        await using var db = Database(); var from = new DateTime(2026, 11, 1);
        var result = await new AdminDashboardRepository(db).GetAsync(from, default);
        Assert.Equal(0, result.TotalAccounts); Assert.Equal(0, result.TotalBatches); Assert.Empty(result.RoleDistribution);
        Assert.Equal(Enumerable.Range(0, 7).Select(offset => from.AddDays(offset)), result.WeeklyActivity.Select(day => day.Date));
        Assert.All(result.WeeklyActivity, day => { Assert.Equal(0, day.AccountCount); Assert.Equal(0, day.BatchCount); });
    }

    [Fact]
    public async Task DashboardSeparatesAllTimeCountsFromWeeklyActivityAndCountsActiveAndPendingIndependently()
    {
        await using var db = Database(); var from = new DateTime(2026, 11, 1);
        var supplierRole = new Role { RoleCode = "SUPPLIER", RoleName = "Supplier" };
        var distributorRole = new Role { RoleCode = "DISTRIBUTOR", RoleName = "Distributor" };
        var supplier = User(7, "ACTIVE", from, supplierRole); var distributor = User(8, "PENDING", from.AddDays(2), distributorRole);
        db.AddRange(supplier, distributor, User(9, "INACTIVE", from.AddDays(-1), supplierRole));
        var activeCrop = new CropType { CropCode = "RICE", CropName = "Rice", CategoryName = "Grain", IsActive = true };
        db.AddRange(activeCrop, new CropType { CropCode = "OLD", CropName = "Old", CategoryName = "Grain", IsActive = false });
        db.AddRange(new Supplier { Account = supplier, SupplierCode = "S-7", SupplierName = "Supplier", TaxCode = "S-TAX", Address = "Address", ContactPerson = "Contact" },
            new Distributor { Account = distributor, DistributorCode = "D-8", DistributorName = "Distributor", TaxCode = "D-TAX" });
        foreach (var (code, status, created) in new[] { ("OLD-BATCH", "PENDING_PREDECLARATION", from.AddDays(-1)), ("QC", "PENDING_QC", from.AddDays(1)), ("STOCK", "IN_STOCK", from.AddDays(3)) })
            db.Add(new ProductBatch { BatchCode = code, ProductName = code, Unit = "Kg", BatchStatus = status, CreatedAt = created, CropType = activeCrop });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = await new AdminDashboardRepository(db).GetAsync(from, default);
        Assert.Equal(3, result.TotalAccounts); Assert.Equal(1, result.ActiveAccounts); Assert.Equal(1, result.PendingAccounts);
        Assert.Equal(2, result.TotalCropTypes); Assert.Equal(1, result.ActiveCropTypes);
        Assert.Equal(3, result.TotalBatches); Assert.Equal(2, result.PendingBatches);
        Assert.Equal(1, result.TotalSuppliers); Assert.Equal(1, result.TotalDistributors);
        Assert.Equal(2, Assert.Single(result.RoleDistribution, item => item.Label == "Supplier").Value);
        Assert.Equal(2, result.WeeklyActivity.Sum(day => day.AccountCount));
        Assert.Equal(2, result.WeeklyActivity.Sum(day => day.BatchCount));
        Assert.Equal(0, result.WeeklyActivity[0].BatchCount); Assert.Equal(1, result.WeeklyActivity[1].BatchCount);
        Assert.Empty(db.ChangeTracker.Entries());
    }
    [Fact]
    public async Task AuditActivityGroupsVietnamDaysAndExcludesLoginsFromOperations()
    {
        await using var db = Database();
        var today = DateTime.UtcNow.AddHours(7).Date;
        var firstUtc = today.AddDays(-6).AddHours(-7);
        db.AuditLogs.AddRange(
            new AuditLog { ActionType = "LOGIN_SUCCESS", EntityName = "ACCOUNT", CreatedAt = firstUtc },
            new AuditLog { ActionType = "LOGIN_FAILED", EntityName = "ACCOUNT", CreatedAt = firstUtc.AddHours(3) },
            new AuditLog { ActionType = "UPDATE", EntityName = "ACCOUNT", CreatedAt = firstUtc.AddHours(23) },
            new AuditLog { ActionType = "CREATE", EntityName = "CONTRACT", CreatedAt = firstUtc.AddDays(1) },
            new AuditLog { ActionType = "DELETE", EntityName = "ACCOUNT", CreatedAt = firstUtc.AddTicks(-1) },
            new AuditLog { ActionType = "UPDATE", EntityName = "ACCOUNT", CreatedAt = firstUtc.AddDays(7) });
        await db.SaveChangesAsync();
        var result = await new AdminDashboardRepository(db).GetAsync(today.AddDays(-6), default);
        Assert.NotNull(result.AuditActivity);
        Assert.Equal(7, result.AuditActivity.Count);
        Assert.Equal(DateOnly.FromDateTime(today.AddDays(-6)), result.AuditActivity[0].Date);
        Assert.Equal(1, result.AuditActivity[0].LoginSuccessCount);
        Assert.Equal(1, result.AuditActivity[0].LoginFailedCount);
        Assert.Equal(1, result.AuditActivity[0].OperationCount);
        Assert.Equal(1, result.AuditActivity[1].OperationCount);
        Assert.All(result.AuditActivity.Skip(2), day => Assert.Equal(0, day.OperationCount));
    }

}
