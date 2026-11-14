using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SAW.Domain.Entities;
using SAW.Infrastructure.Notifications;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.Notifications;

public sealed class SupplyContractDeliveryReminderWorkerTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        public ServiceProvider Services { get; }
        public Fixture()
        {
            var name = Guid.NewGuid().ToString();
            Services = new ServiceCollection().AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(name)).BuildServiceProvider();
        }
        public AppDbContext Database() => new(Services.GetRequiredService<DbContextOptions<AppDbContext>>());
        public async Task<SupplyContract> SeedAsync(string status = "APPROVED", int startDays = 0, int endDays = 20, string period = "WEEK")
        {
            await using var db = Database();
            var supplierAccount = new Account { AccountId = 1, Username = "supplier", Email = "supplier@test.invalid", FullName = "Supplier", PasswordHash = "hash",
                AccountStatus = "ACTIVE", Role = new Role { RoleId = 1, RoleCode = "SUPPLIER", RoleName = "Supplier" } };
            var manager = new Account { AccountId = 2, Username = "manager", Email = "manager@test.invalid", FullName = "Manager", PasswordHash = "hash",
                AccountStatus = "ACTIVE", Role = new Role { RoleId = 2, RoleCode = "WAREHOUSE_MANAGER", RoleName = "Manager" } };
            var contract = new SupplyContract { SupplyContractId = 10, ContractCode = "SC-10", WarehouseManagerAccount = manager,
                Supplier = new Supplier { SupplierId = 1, Account = supplierAccount, SupplierCode = "SUP-1", SupplierName = "Supplier One", TaxCode = "0123456789", ContactPerson = "Contact", Address = "Address" },
                ContractStatus = status, DeliveryQuantityKg = 100, StartDate = Today.AddDays(startDays), EndDate = Today.AddDays(endDays), DeliveryPeriod = period };
            db.Add(contract); await db.SaveChangesAsync();
            db.Notifications.RemoveRange(db.Notifications); await db.SaveChangesAsync();
            return contract;
        }
        public async Task ReceiptAsync(decimal weight, string status = "COMMITTED", int expectedDays = 0, long contractId = 10)
        {
            await using var db = Database();
            db.GoodsReceipts.Add(new GoodsReceipt { ReceiptCode = Guid.NewGuid().ToString(), WeightInKg = weight, Unit = "kg", ReceiptStatus = status,
                ProductBatch = new ProductBatch { BatchCode = Guid.NewGuid().ToString(), ProductName = "Produce", Unit = "kg", SupplierId = 1,
                    SupplyContractId = contractId, ExpectedDeliveryDate = Today.AddDays(expectedDays), DeclaredQuantity = 1000, WeightInKg = 1000 } });
            await db.SaveChangesAsync();
        }
        public async Task RunAsync()
        {
            using var worker = new SupplyContractDeliveryReminderWorker(Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SupplyContractDeliveryReminderWorker>.Instance);
            var method = typeof(SupplyContractDeliveryReminderWorker).GetMethod("CreateDueRemindersAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(worker, [CancellationToken.None])!;
        }
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    [Theory]
    [InlineData("PENDING_SUPPLIER", 0, 20)]
    [InlineData("PENDING_ADMIN", 0, 20)]
    [InlineData("CANCELLED", 0, 20)]
    [InlineData("APPROVED", 1, 20)]
    [InlineData("APPROVED", -20, -1)]
    public async Task OnlyApprovedContractsInsideTheirEffectiveDatesArePolled(string status, int start, int end)
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync(status, start, end);
        await fixture.RunAsync(); await using var db = fixture.Database();
        Assert.Empty(await db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task CurrentPeriodCountsOnlyCommittedReceiptWeightForThatContractAndPeriod()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync();
        await fixture.ReceiptAsync(25); await fixture.ReceiptAsync(5);
        await fixture.ReceiptAsync(900, "DRAFT"); await fixture.ReceiptAsync(900, contractId: 99);
        await fixture.ReceiptAsync(900, expectedDays: 7);
        await fixture.RunAsync(); await using var db = fixture.Database();
        var reminder = Assert.Single(await db.Notifications.Where(n => n.NotificationType == "SUPPLY_CONTRACT_DELIVERY_REMINDER").ToListAsync());
        Assert.Equal(1, reminder.AccountId); Assert.Equal("HIGH", reminder.Priority); Assert.False(reminder.IsRead);
        Assert.Equal($"10:{fixture.Today.AddDays(6):yyyyMMdd}", reminder.RelatedEntityId);
        Assert.Contains("30/100 kg", reminder.Message); Assert.Contains("70 kg", reminder.Message);
        Assert.Empty(await db.Notifications.Where(n => n.AccountId == 2 && n.NotificationType.StartsWith("SUPPLY_CONTRACT_DELIVERY")).ToListAsync());
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    public async Task FulfilledPeriodDoesNotCreateReminder(decimal received)
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync(); await fixture.ReceiptAsync(received);
        await fixture.RunAsync(); await using var db = fixture.Database();
        Assert.Empty(await db.Notifications.Where(n => n.NotificationType.StartsWith("SUPPLY_CONTRACT_DELIVERY")).ToListAsync());
    }

    [Fact]
    public async Task OnlyLatestUnfulfilledOverduePeriodAlertsBothPartiesAndCurrentPeriodAlertsSupplier()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync(startDays: -15);
        // The latest overdue period [-8,-2] was fulfilled, so the older [-15,-9] remains overdue.
        await fixture.ReceiptAsync(100, expectedDays: -3);
        await fixture.RunAsync(); await using var db = fixture.Database();
        var overdue = await db.Notifications.Where(n => n.NotificationType == "SUPPLY_CONTRACT_DELIVERY_OVERDUE").ToListAsync();
        Assert.Equal(new[] { 1, 2 }, overdue.Select(n => n.AccountId).Order());
        Assert.All(overdue, n => { Assert.Equal($"10:{fixture.Today.AddDays(-9):yyyyMMdd}", n.RelatedEntityId); Assert.Equal("HIGH", n.Priority); });
        var current = Assert.Single(await db.Notifications.Where(n => n.NotificationType == "SUPPLY_CONTRACT_DELIVERY_REMINDER").ToListAsync());
        Assert.Equal(1, current.AccountId); Assert.Equal($"10:{fixture.Today.AddDays(5):yyyyMMdd}", current.RelatedEntityId);
    }

    [Fact]
    public async Task RepeatedPollRefreshesProgressWithoutDuplicatingOrResettingReadReceipt()
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync(); await fixture.RunAsync();
        DateTime created; DateTime readAt = DateTime.UtcNow.AddMinutes(-1); long id;
        await using (var db = fixture.Database())
        {
            var item = await db.Notifications.SingleAsync(n => n.NotificationType == "SUPPLY_CONTRACT_DELIVERY_REMINDER");
            id = item.NotificationId; created = item.CreatedAt; item.IsRead = true; item.ReadAt = readAt; await db.SaveChangesAsync();
        }
        await fixture.ReceiptAsync(40); await fixture.RunAsync(); await fixture.RunAsync();
        await using var read = fixture.Database();
        var refreshed = Assert.Single(await read.Notifications.Where(n => n.NotificationType == "SUPPLY_CONTRACT_DELIVERY_REMINDER").ToListAsync());
        Assert.Equal(id, refreshed.NotificationId); Assert.Equal(created, refreshed.CreatedAt);
        Assert.True(refreshed.IsRead); Assert.Equal(readAt, refreshed.ReadAt);
        Assert.Contains("40/100 kg", refreshed.Message); Assert.Contains("60 kg", refreshed.Message);
    }

    [Theory]
    [InlineData("WEEK")]
    [InlineData("MONTH")]
    public async Task FinalShortPeriodUsesContractEndAndIsDueOnThatDay(string period)
    {
        await using var fixture = new Fixture(); await fixture.SeedAsync(endDays: 0, period: period);
        await fixture.RunAsync(); await using var db = fixture.Database();
        var item = Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal($"10:{fixture.Today:yyyyMMdd}", item.RelatedEntityId);
        Assert.Contains("hôm nay", item.Title); Assert.Contains("100 kg", item.Message);
    }
}
