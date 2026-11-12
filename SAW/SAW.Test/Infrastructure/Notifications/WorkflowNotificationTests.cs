using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.Notifications;

public sealed class WorkflowNotificationTests
{
    private static async Task<AppDbContext> DatabaseAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var roles = new[] { "SUPPLIER", "DISTRIBUTOR", "WAREHOUSE_MANAGER", "OPERATION_STAFF", "QC_STAFF", "ADMINISTRATOR" };
        for (var index = 0; index < roles.Length; index++)
            db.Accounts.Add(new Account { AccountId = index + 1, Username = $"user{index}", FullName = $"User {index}", Email = $"user{index}@test.invalid", PasswordHash = "hash", AccountStatus = "ACTIVE",
                Role = new Role { RoleId = index + 1, RoleCode = roles[index], RoleName = roles[index] } });
        db.Accounts.AddRange(new Account { AccountId = 7, RoleId = 3, Username = "inactive", FullName = "Inactive manager", Email = "inactive@test.invalid", PasswordHash = "hash", AccountStatus = "INACTIVE" },
            new Account { AccountId = 8, RoleId = 2, Username = "other", FullName = "Other customer", Email = "other@test.invalid", PasswordHash = "hash", AccountStatus = "ACTIVE" });
        db.Suppliers.Add(new Supplier { SupplierId = 1, AccountId = 1, SupplierCode = "SUP", SupplierName = "Supplier", TaxCode = "0123456789", ContactPerson = "Contact", Address = "Address" });
        db.Distributors.AddRange(new Distributor { DistributorId = 1, AccountId = 2, DistributorCode = "D1", DistributorName = "Customer", TaxCode = "1234567890" },
            new Distributor { DistributorId = 2, AccountId = 8, DistributorCode = "D2", DistributorName = "Other", TaxCode = "1234567891" });
        await db.SaveChangesAsync();
        db.ProductBatches.Add(new ProductBatch { ProductBatchId = 10, SupplierId = 1, BatchCode = "B-10", ProductName = "Produce", Unit = "kg", BatchStatus = "SUBMITTED" });
        db.PurchaseOrders.Add(new PurchaseOrder { PurchaseOrderId = 20, DistributorId = 1, OrderCode = "O-20", DeliveryAddress = "Address", OrderStatus = "PENDING" });
        db.SupplyContracts.Add(new SupplyContract { SupplyContractId = 30, SupplierId = 1, WarehouseManagerAccountId = 3, ContractCode = "C-30", ContractStatus = "PENDING_SUPPLIER" });
        await db.SaveChangesAsync(); await ClearAsync(db); return db;
    }
    private static async Task ClearAsync(AppDbContext db) { db.Notifications.RemoveRange(db.Notifications); await db.SaveChangesAsync(); }
    private static async Task<int[]> Recipients(AppDbContext db, string type) => await db.Notifications.Where(n => n.NotificationType == type).OrderBy(n => n.AccountId).Select(n => n.AccountId).ToArrayAsync();

    [Theory]
    [InlineData("PENDING_QC", "QC_REQUIRED", 5)]
    [InlineData("APPROVED_FOR_STORAGE", "BATCH_ACTION", 4)]
    [InlineData("QUARANTINE", "BATCH_ACTION", 4)]
    [InlineData("IN_STOCK", "LOT_PRICING", 3)]
    public async Task BatchTransitionNotifiesOwnerAndResponsibleActiveRole(string status, string type, int roleAccount)
    {
        await using var db = await DatabaseAsync(); var batch = await db.ProductBatches.FindAsync(10L); batch!.BatchStatus = status; await db.SaveChangesAsync();
        Assert.Equal(new[] { 1 }, await Recipients(db, "BATCH_STATUS")); Assert.Equal(new[] { roleAccount }, await Recipients(db, type));
        Assert.Equal(2, await db.Notifications.CountAsync());
        Assert.All(await db.Notifications.ToListAsync(), n => { Assert.Contains("B-10", n.Message); Assert.False(n.IsRead); });
        var count = await db.Notifications.CountAsync(); await db.SaveChangesAsync(); Assert.Equal(count, await db.Notifications.CountAsync());
        batch.Note = "An unrelated note"; await db.SaveChangesAsync(); Assert.Equal(count, await db.Notifications.CountAsync());
    }

    [Theory]
    [InlineData("APPROVED", true)]
    [InlineData("RESERVED", true)]
    [InlineData("PICKED", true)]
    [InlineData("DISPATCHED", false)]
    [InlineData("DELIVERED", false)]
    [InlineData("REJECTED", false)]
    [InlineData("CANCELLED", false)]
    public async Task OrderTransitionKeepsCustomerScopeAndOnlyPromptsOperationsAtActionableStages(string status, bool operation)
    {
        await using var db = await DatabaseAsync(); var order = await db.PurchaseOrders.FindAsync(20L); order!.OrderStatus = status; order.RejectionReason = "Reason for customer"; await db.SaveChangesAsync();
        Assert.Equal(new[] { 2, 3 }, await Recipients(db, "ORDER_STATUS"));
        Assert.Equal(operation ? new[] { 4 } : [], await Recipients(db, "ORDER_FULFILLMENT"));
        Assert.DoesNotContain(await db.Notifications.ToListAsync(), n => n.AccountId is 7 or 8);
        Assert.Contains("Reason for customer", (await db.Notifications.SingleAsync(n => n.AccountId == 2)).Message);
    }

    [Theory]
    [InlineData("DELIVERY_REFUSED")]
    [InlineData("PARTIALLY_REFUSED")]
    public async Task RefusalStatusAloneDoesNotDuplicateTheSeparateRefusalEvent(string status)
    {
        await using var db = await DatabaseAsync(); (await db.PurchaseOrders.FindAsync(20L))!.OrderStatus = status; await db.SaveChangesAsync();
        Assert.Empty(await db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task ReceiptOfRemainingLotsInPartiallyRefusedOrderStillNotifiesCustomerAndManager()
    {
        await using var db = await DatabaseAsync(); var order = (await db.PurchaseOrders.FindAsync(20L))!;
        order.OrderStatus = "PARTIALLY_REFUSED"; await db.SaveChangesAsync(); order.ReceivedAt = DateTime.UtcNow; await db.SaveChangesAsync();
        Assert.Equal(new[] { 2, 3 }, await Recipients(db, "ORDER_STATUS"));
        Assert.Contains("các lô còn lại", (await db.Notifications.FirstAsync()).Message);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("revision")]
    public async Task ContractStatusOrNewRevisionNotifiesSupplierAdministratorAndAssignedManager(string change)
    {
        await using var db = await DatabaseAsync(); var contract = (await db.SupplyContracts.FindAsync(30L))!;
        if (change == "status") contract.ContractStatus = "PENDING_ADMIN"; else contract.Revision++;
        await db.SaveChangesAsync(); Assert.Equal(new[] { 1, 3, 6 }, await Recipients(db, "CONTRACT_STATUS"));
        Assert.All(await db.Notifications.ToListAsync(), n => { Assert.Equal("SUPPLY_CONTRACT", n.RelatedEntityType); Assert.Equal("30", n.RelatedEntityId); });
        await ClearAsync(db); contract.Terms = "Terms only"; await db.SaveChangesAsync(); Assert.Empty(await db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task PriceChangeAndStoppingPublicationNotifyActiveManagersWithDifferentActions()
    {
        await using var db = await DatabaseAsync(); var offer = new BatchSaleOffer { ProductBatchId = 10, WholeLotPrice = 1000000, IsPublished = true }; db.Add(offer); await db.SaveChangesAsync();
        Assert.Equal(new[] { 3 }, await Recipients(db, "SALE_PRICE_UPDATED")); Assert.Contains("B-10", (await db.Notifications.SingleAsync()).Message);
        await ClearAsync(db); offer.IsPublished = false; await db.SaveChangesAsync();
        Assert.Equal(new[] { 3 }, await Recipients(db, "LOT_PRICING")); Assert.Empty(await Recipients(db, "SALE_PRICE_UPDATED"));
    }

    [Theory]
    [InlineData("DRAFT", false, false)]
    [InlineData("SENT", false, false)]
    [InlineData("SENT", true, true)]
    [InlineData("ACCEPTED", true, true)]
    [InlineData("DECLINED", true, true)]
    [InlineData("WITHDRAWN", true, true)]
    public async Task ProposalNotificationRequiresSentNonDraftAndStaysWithinAffectedOrder(string status, bool sent, bool notify)
    {
        await using var db = await DatabaseAsync();
        var adjustment = new StockAdjustment { StockAdjustmentId = 40, AdjustmentCode = "SA-40", CreatedByAccountId = 4, AdjustmentType = "LOSS", Reason = "Loss" };
        var resolution = new StockAdjustmentResolution { StockAdjustment = adjustment, PurchaseOrderId = 20, ExpectedInventorySnapshotJson = "{}" };
        db.Add(resolution); await db.SaveChangesAsync(); await ClearAsync(db);
        var proposal = new StockAdjustmentProposal { Resolution = resolution, ScopeSnapshotJson = "{}", CustomerMessage = "Revised proposal", ProposalStatus = status, SentAt = sent ? DateTime.UtcNow : null };
        db.Add(proposal); await db.SaveChangesAsync();
        Assert.Equal(notify ? new[] { 2, 3 } : [], await Recipients(db, "ORDER_PROPOSAL"));
        Assert.DoesNotContain(await db.Notifications.ToListAsync(), n => n.AccountId is 7 or 8);
        var count = await db.Notifications.CountAsync(); proposal.CustomerMessage = "Unsent text edit"; await db.SaveChangesAsync(); Assert.Equal(count, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task ResolutionNotifiesStaffAndAffectedCustomerWithoutDuplicatingCreatorWhoIsAlreadyStaff()
    {
        await using var db = await DatabaseAsync(); var adjustment = new StockAdjustment { StockAdjustmentId = 40, AdjustmentCode = "SA-40", CreatedByAccountId = 4, AdjustmentType = "LOSS", Reason = "Loss" };
        var resolution = new StockAdjustmentResolution { StockAdjustment = adjustment, PurchaseOrderId = 20, ExpectedInventorySnapshotJson = "{}" };
        db.Add(resolution); await db.SaveChangesAsync(); await ClearAsync(db);
        resolution.State = "CUSTOMER_ACCEPTED"; await db.SaveChangesAsync();
        Assert.Equal(new[] { 3, 4 }, await Recipients(db, "ADJUSTMENT_RESOLUTION")); Assert.Equal(new[] { 2 }, await Recipients(db, "ORDER_INCIDENT"));
        Assert.Equal(3, await db.Notifications.CountAsync());
    }
}
