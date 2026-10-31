using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.API.Controllers;
using SAW.Application.Exceptions;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.DistributorOrders;

public sealed class OrderReturnSqlTests : IClassFixture<DistributorSqlFixture>
{
    private readonly ReceivingSqlFixture fixture;
    public OrderReturnSqlTests(DistributorSqlFixture fixture) => this.fixture = fixture.Database;
    private static OrderReturnsController Controller(AppDbContext db, int id, string role) => new(db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("account_id", id.ToString()), new Claim(ClaimTypes.Role, role)], "test")) } }
    };
    private async Task<Account> AccountAsync(AppDbContext db, string role)
    {
        var entity = await db.Roles.SingleOrDefaultAsync(r => r.RoleCode == role);
        if (entity == null) { entity = new Role { RoleCode = role, RoleName = role, IsActive = true }; db.Roles.Add(entity); }
        var key = Guid.NewGuid().ToString("N");
        var account = new Account { Role = entity, Username = key, Email = key + "@test.invalid", FullName = role, PasswordHash = "test", AccountStatus = "ACTIVE" };
        db.Accounts.Add(account); await db.SaveChangesAsync(); return account;
    }

    [ReceivingSqlFact]
    public async Task MissingReturnMigration_ShowsReadinessMessageInsteadOfUnexpectedError()
    {
        await using var db = fixture.Context(retry: false);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ORDER_RETURN");
        var error = await Assert.ThrowsAsync<ConflictException>(() => Controller(db, fixture.ActorId, "DISTRIBUTOR").List(default));
        Assert.Contains("chưa sẵn sàng", error.Message);
        await transaction.RollbackAsync();
    }

    [ReceivingSqlFact]
    public async Task WholeOrderReturn_IsOwned_Idempotent_AndStopsAtRequestWithoutChangingOrderOrStock()
    {
        await using var setup = fixture.Context();
        var buyer = await AccountAsync(setup, "DISTRIBUTOR");
        var other = await AccountAsync(setup, "DISTRIBUTOR");
        var manager = await AccountAsync(setup, "WAREHOUSE_MANAGER");
        var staff = await AccountAsync(setup, "OPERATION_STAFF");
        var order = new PurchaseOrder { Distributor = new Distributor { AccountId = buyer.AccountId, DistributorCode = buyer.Username[..20], DistributorName = "Buyer", TaxCode = buyer.Username, ProfileStatus = "ACTIVE" },
            OrderCode = "RETURN-" + Guid.NewGuid().ToString("N"), OrderStatus = "ISSUED", DeliveryAddress = "Test", CreatedAt = DateTime.UtcNow };
        order.GoodsIssues.Add(new GoodsIssue { IssueCode = "ISSUE-" + Guid.NewGuid().ToString("N"), OperationAccountId = staff.AccountId, IssueStatus = "COMMITTED" });
        setup.PurchaseOrders.Add(order); await setup.SaveChangesAsync();
        var id = order.PurchaseOrderId;
        var inventoryBefore = await setup.Inventories.SumAsync(i => (decimal?)i.QuantityOnHand) ?? 0;
        await using (var db = fixture.Context())
            await Assert.ThrowsAsync<NotFoundException>(() => Controller(db, other.AccountId, "DISTRIBUTOR").RequestReturn(id, new ReturnNote("Hư hỏng"), default));
        await using (var db = fixture.Context())
            await Controller(db, buyer.AccountId, "DISTRIBUTOR").RequestReturn(id, new ReturnNote("Hư hỏng"), default);
        var notesAfterFirstRequest = await setup.Notifications.CountAsync();
        await using (var db = fixture.Context())
            await Controller(db, buyer.AccountId, "DISTRIBUTOR").RequestReturn(id, new ReturnNote("Hư hỏng"), default);
        Assert.Equal(1, await setup.OrderReturns.CountAsync(r => r.PurchaseOrderId == id));
        Assert.Equal(notesAfterFirstRequest, await setup.Notifications.CountAsync());
        Assert.True(await setup.Notifications.AnyAsync(n => n.AccountId == buyer.AccountId && n.NotificationType == "ORDER_RETURN"));
        Assert.True(await setup.Notifications.AnyAsync(n => n.AccountId == manager.AccountId && n.NotificationType == "ORDER_RETURN"));
        await using (var db = fixture.Context())
        {
            Assert.IsType<OkObjectResult>(await Controller(db, buyer.AccountId, "DISTRIBUTOR").Get(id, default));
            Assert.IsType<OkObjectResult>(await Controller(db, buyer.AccountId, "DISTRIBUTOR").List(default));
            await Assert.ThrowsAsync<NotFoundException>(() => Controller(db, other.AccountId, "DISTRIBUTOR").Get(id, default));
        }
        await using (var db = fixture.Context())
            await Assert.ThrowsAsync<ConflictException>(() => Controller(db, buyer.AccountId, "DISTRIBUTOR").RequestReturn(id, new ReturnNote("Lý do khác"), default));
        await setup.Entry(order).ReloadAsync();
        Assert.Equal("ISSUED", order.OrderStatus);
        var request = await setup.OrderReturns.SingleAsync(r => r.PurchaseOrderId == id);
        Assert.Equal("REQUESTED", request.Status);
        Assert.Equal(buyer.AccountId, request.RequestedByAccountId);
        Assert.Null(request.ReviewedAt); Assert.Null(request.ReceivedAt);
        Assert.Equal(inventoryBefore, await setup.Inventories.SumAsync(i => (decimal?)i.QuantityOnHand) ?? 0);
        Assert.False(await setup.OrderStatusHistories.AnyAsync(h => h.PurchaseOrderId == id && h.NewStatus == "RETURNED"));
    }

    [ReceivingSqlFact]
    public async Task BatchTransitions_NotifyCorrectRoles_OnlyAfterCommit()
    {
        await using var db = fixture.Context(retry: false);
        var qc = await AccountAsync(db, "QC_STAFF"); var staff = await AccountAsync(db, "OPERATION_STAFF");
        var batch = await fixture.BatchAsync();
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == qc.AccountId && n.NotificationType == "QC_REQUIRED" && n.Message.Contains(batch.BatchCode)));
        db.Attach(batch); batch.BatchStatus = "APPROVED_FOR_STORAGE";
        await using (var tx = await db.Database.BeginTransactionAsync())
        { await db.SaveChangesAsync(); await tx.RollbackAsync(); }
        db.ChangeTracker.Clear();
        Assert.False(await db.Notifications.AnyAsync(n => n.AccountId == staff.AccountId && n.Message.Contains(batch.BatchCode)));
        var saved = await db.ProductBatches.FindAsync(batch.ProductBatchId); saved!.BatchStatus = "APPROVED_FOR_STORAGE"; await db.SaveChangesAsync();
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == staff.AccountId && n.Message.Contains(batch.BatchCode)));
        var count = await db.Notifications.CountAsync(); await db.SaveChangesAsync(); Assert.Equal(count, await db.Notifications.CountAsync());
    }

    [ReceivingSqlFact]
    public async Task ContractAndStockIncidents_ReachParticipants_NotOtherCustomers()
    {
        await using var db = fixture.Context();
        // The exported schema has these computed columns; GenerateCreateScript's
        // unspecified SQL expressions cannot recreate them in this fixture.
        await db.Database.ExecuteSqlRawAsync("""
            IF COLUMNPROPERTY(OBJECT_ID('STOCK_ADJUSTMENT'), 'DifferenceQuantity', 'IsComputed') = 0
            BEGIN
                ALTER TABLE STOCK_ADJUSTMENT DROP COLUMN DifferenceQuantity, VariancePercent;
                ALTER TABLE STOCK_ADJUSTMENT ADD DifferenceQuantity AS (ActualQuantity-SystemQuantity) PERSISTED;
                ALTER TABLE STOCK_ADJUSTMENT ADD VariancePercent AS (CASE WHEN SystemQuantity=0 THEN NULL ELSE CONVERT(decimal(9,2),ABS((ActualQuantity-SystemQuantity)*100/SystemQuantity)) END) PERSISTED;
            END
            """);
        var manager = await AccountAsync(db, "WAREHOUSE_MANAGER"); var admin = await AccountAsync(db, "ADMINISTRATOR");
        var staff = await AccountAsync(db, "OPERATION_STAFF"); var buyer = await AccountAsync(db, "DISTRIBUTOR"); var other = await AccountAsync(db, "DISTRIBUTOR");
        var code = "CT-" + Guid.NewGuid().ToString("N");
        var contract = new SupplyContract { ContractCode = code, WarehouseManagerAccountId = manager.AccountId, SupplierId = fixture.SupplierId,
            CropTypeId = fixture.CropTypeId, DeliveryQuantityKg = 100, UnitPriceVndPerKg = 1000, StartDate = new(2026, 1, 1), EndDate = new(2026, 12, 31), CreatedAt = DateTime.UtcNow };
        db.SupplyContracts.Add(contract); await db.SaveChangesAsync();
        foreach (var id in new[] { manager.AccountId, admin.AccountId, fixture.ActorId })
            Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == id && n.NotificationType == "CONTRACT_STATUS" && n.Message.Contains(code)));
        Assert.False(await db.Notifications.AnyAsync(n => n.AccountId == other.AccountId && n.Message.Contains(code)));
        contract.ContractStatus = "APPROVED"; await db.SaveChangesAsync();
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == fixture.ActorId && n.Message.Contains(code) && n.Message.Contains("Admin đã duyệt")));

        var batch = await fixture.BatchAsync();
        var stock = new Inventory { ProductBatchId = batch.ProductBatchId, QuantityOnHand = 100, WarehouseLocation = new WarehouseLocation { LocationCode = Guid.NewGuid().ToString("N")[..20], ZoneName = "Return test" } };
        var order = new PurchaseOrder { OrderCode = "INC-" + Guid.NewGuid().ToString("N"), OrderStatus = "APPROVED", DeliveryAddress = "Test",
            Distributor = new Distributor { AccountId = buyer.AccountId, DistributorCode = buyer.Username[..20], DistributorName = "Buyer", TaxCode = buyer.Username } };
        order.OrderDetails.Add(new OrderDetail { CropTypeId = fixture.CropTypeId, RequestedProductBatchId = batch.ProductBatchId, RequestedQuantity = 1, RequestedWeightKg = 100, Unit = "lot", UnitPrice = 1000 });
        db.Inventories.Add(stock); db.PurchaseOrders.Add(order); await db.SaveChangesAsync();
        var adjustmentCode = "ADJ-" + Guid.NewGuid().ToString("N");
        var adjustment = new StockAdjustment { AdjustmentCode = adjustmentCode, InventoryId = stock.InventoryId, CreatedByAccountId = staff.AccountId,
            SystemQuantity = 100, ActualQuantity = 90, AdjustmentType = "DECREASE", Reason = "Hao hụt", CreatedAt = DateTime.UtcNow };
        db.StockAdjustments.Add(adjustment); await db.SaveChangesAsync();
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == buyer.AccountId && n.NotificationType == "ORDER_INCIDENT"));
        Assert.False(await db.Notifications.AnyAsync(n => n.AccountId == other.AccountId && n.NotificationType == "ORDER_INCIDENT"));
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == manager.AccountId && n.Message.Contains(adjustmentCode)));
        var resolution = new StockAdjustmentResolution { StockAdjustmentId = adjustment.StockAdjustmentId, PurchaseOrderId = order.PurchaseOrderId,
            ExpectedInventorySnapshotJson = "{}", UpdatedAt = DateTime.UtcNow };
        db.StockAdjustmentResolutions.Add(resolution); await db.SaveChangesAsync();
        var proposal = new StockAdjustmentProposal { StockAdjustmentId = adjustment.StockAdjustmentId, Resolution = resolution,
            ScopeSnapshotJson = "{}", Revision = 1, OriginalWeightKg = 100, ProposedWeightKg = 90, OriginalWholeLotPrice = 1000, ProposedWholeLotPrice = 900,
            OriginalOrderTotal = 1000, ProposedOrderTotal = 900, CustomerMessage = "Điều chỉnh sau hao hụt", CreatedByAccountId = manager.AccountId, CreatedAt = DateTime.UtcNow };
        db.StockAdjustmentProposals.Add(proposal); await db.SaveChangesAsync();
        Assert.False(await db.Notifications.AnyAsync(n => n.AccountId == buyer.AccountId && n.NotificationType == "ORDER_PROPOSAL"));
        proposal.ProposalStatus = "SENT"; proposal.SentAt = DateTime.UtcNow; await db.SaveChangesAsync();
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == buyer.AccountId && n.NotificationType == "ORDER_PROPOSAL"));
        adjustment.AdjustmentStatus = "APPROVED"; adjustment.ReviewNote = "Đã xử lý"; await db.SaveChangesAsync();
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == staff.AccountId && n.Message.Contains(adjustmentCode) && n.Message.Contains("Đã xử lý")));
    }
}

public class OrderReturnMigrationSqlTests
{
    [ReceivingSqlFact]
    public async Task Migration_CreatesReturnTable_AndKeepsExistingHistoryTrigger()
    {
        var fixture = new ReceivingSqlFixture(useMigrationBaseline: true);
        try
        {
            await fixture.InitializeAsync();
            await using var db = fixture.Context();
            Assert.Empty(await db.OrderReturns.ToListAsync());
            Assert.Contains("20261029150053_AddOrderReturnWorkflow", await db.Database.GetAppliedMigrationsAsync());
            var triggers = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.triggers WHERE name = 'TR_PURCHASE_ORDER_STATUS_HISTORY'").SingleAsync();
            Assert.Equal(1, triggers);
        }
        finally { await fixture.DisposeAsync(); }
    }
}
