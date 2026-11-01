using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SAW.API.Controllers;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.DistributorOrders;

public sealed class DeliveryRefusalSqlTests(DistributorSqlFixture fixture) : IClassFixture<DistributorSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Database;
    private static readonly byte[] Photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7X8AAAAASUVORK5CYII=");
    private long[] selectedBatches = [];
    private sealed record Envelope<T>(T Data);
    private DistributorOrderRepository Repository() => new(new DbContextOptionsBuilder<SAW.Infrastructure.Persistence.AppDbContext>()
        .UseSqlServer(F.ConnectionString, sql => sql.EnableRetryOnFailure()).Options, new HttpContextAccessor());

    private async Task<int> Account(string roleCode)
    {
        await using var db = F.Context();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.RoleCode == roleCode);
        if (role is null) { role = new Role { RoleCode = roleCode, RoleName = roleCode, IsActive = true }; db.Roles.Add(role); }
        var key = Guid.NewGuid().ToString("N");
        var account = new Account { Role = role, Username = key, FullName = roleCode, Email = key + "@test.invalid", PasswordHash = "test", AccountStatus = "ACTIVE" };
        db.Accounts.Add(account);
        if (roleCode == "DISTRIBUTOR") db.Distributors.Add(new Distributor { Account = account, DistributorCode = key[..20], DistributorName = "Test", TaxCode = key, ProfileStatus = "ACTIVE" });
        await db.SaveChangesAsync();
        return account.AccountId;
    }

    private async Task<long> Order(int buyer, string status = "DISPATCHED", bool committed = true)
    {
        var batches = new[] { await F.BatchAsync(), await F.BatchAsync() };
        await using var db = F.Context();
        var owner = await db.Distributors.SingleAsync(d => d.AccountId == buyer);
        var order = new PurchaseOrder { DistributorId = owner.DistributorId, OrderCode = "REFUSE-" + Guid.NewGuid().ToString("N"),
            OrderStatus = status, DeliveryAddress = "Test", ContactPhone = "0901234567", CreatedAt = DateTime.UtcNow,
            SubtotalAmount = 3000000, TotalAmount = 3000000,
            ReceivedAt = status == "DELIVERED" ? DateTime.UtcNow.AddMinutes(-1) : null,
            ReceivedByAccountId = status == "DELIVERED" ? buyer : null };
        var issue = new GoodsIssue { PurchaseOrder = order, IssueCode = Guid.NewGuid().ToString("N"), OperationAccountId = F.ActorId,
            IssueStatus = committed ? "COMMITTED" : "DRAFT", IssuedAt = DateTime.UtcNow };
        foreach (var batch in batches)
        {
            db.Attach(batch); batch.BatchStatus = "ISSUED";
            var stock = new Inventory { ProductBatchId = batch.ProductBatchId, QuantityOnHand = 0, Unit = "kg",
                WarehouseLocation = new WarehouseLocation { LocationCode = Guid.NewGuid().ToString("N")[..20], ZoneName = "Test" } };
            var line = new OrderDetail { PurchaseOrder = order, RequestedProductBatchId = batch.ProductBatchId, CropTypeId = F.CropTypeId,
                RequestedQuantity = 1, ApprovedQuantity = 1, RequestedWeightKg = 100, ApprovedWeightKg = 100, Unit = "lot", UnitPrice = 1500000 };
            var reservation = new InventoryReservation { OrderDetail = line, Inventory = stock, ReservedQuantity = 100, PickedQuantity = 100,
                ReservationStatus = "ISSUED", ReservedAt = DateTime.UtcNow };
            db.GoodsIssueDetails.Add(new GoodsIssueDetail { GoodsIssue = issue, OrderDetail = line, InventoryReservation = reservation,
                Inventory = stock, IssuedQuantity = 1, Unit = "lot", WeightInKg = 100 });
            db.BatchSaleOffers.Add(new BatchSaleOffer { ProductBatchId = batch.ProductBatchId, WholeLotPrice = 1500000, IsPublished = false, UpdatedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();
        selectedBatches = batches.Select(b => b.ProductBatchId).ToArray();
        return order.PurchaseOrderId;
    }

    private static AuthenticationHeaderValue Bearer(int id, string role = "DISTRIBUTOR") => new("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "uc28-test", "uc28-test", [new Claim("account_id", id.ToString()), new Claim(ClaimTypes.Role, role)],
        expires: DateTime.UtcNow.AddMinutes(20), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256))));

    private MultipartFormDataContent Form(Guid? requestId = null, string reason = "Hàng hỏng khi giao", int count = 1, byte[]? image = null, string name = "proof.png", IEnumerable<long>? batches = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent((requestId ?? Guid.NewGuid()).ToString()), "requestId");
        form.Add(new StringContent(reason), "reason");
        foreach (var batch in batches ?? selectedBatches) form.Add(new StringContent(batch.ToString()), "batchIds");
        for (var i = 0; i < count; i++)
        {
            var content = new ByteArrayContent(image ?? Photo);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "images", name);
        }
        return form;
    }

    [ReceivingSqlFact]
    public async Task WholeOrderRefusal_AfterReceipt_PersistsEvidenceOnce_NotifiesManager_AndNeverRestocks()
    {
        var buyer = await Account("DISTRIBUTOR"); var manager = await Account("WAREHOUSE_MANAGER"); var other = await Account("DISTRIBUTOR");
        var id = await Order(buyer, "DELIVERED");
        using var client = await F.ApiAsync(); client.DefaultRequestHeaders.Authorization = Bearer(buyer);
        var requestId = Guid.NewGuid();
        await using var db = F.Context();
        var receivedAt = (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.PurchaseOrderId == id)).ReceivedAt;
        var stockBefore = await db.Inventories.SumAsync(i => i.QuantityOnHand);
        var receiptCount = await db.GoodsReceipts.CountAsync();
        using var form = Form(requestId, count: 2);
        using var result = await client.PostAsync($"/api/order-delivery-refusals/{id}", form);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var view = (await result.Content.ReadFromJsonAsync<Envelope<DeliveryRefusalView>>())!.Data;
        Assert.Equal(2, view.Images.Count); Assert.Equal(id, view.PurchaseOrderId);
        Assert.Equal("Hàng hỏng khi giao", view.Reason);
        var noteCount = await db.Notifications.CountAsync(n => n.NotificationType == "DELIVERY_REFUSAL" && n.RelatedEntityId == id.ToString());
        Assert.Equal(2, noteCount); // Customer + manager, no duplicate generic status alert.
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == manager && n.NotificationType == "DELIVERY_REFUSAL" && n.RelatedEntityId == id.ToString()));
        using var retry = Form(requestId, count: 2);
        using var repeated = await client.PostAsync($"/api/order-delivery-refusals/{id}", retry);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(noteCount, await db.Notifications.CountAsync(n => n.NotificationType == "DELIVERY_REFUSAL" && n.RelatedEntityId == id.ToString()));
        Assert.Equal(1, await db.OrderDeliveryRefusals.CountAsync(r => r.PurchaseOrderId == id));
        Assert.Equal(2, await db.OrderDeliveryRefusalImages.CountAsync(i => i.OrderDeliveryRefusal.PurchaseOrderId == id));
        Assert.Equal(1, await db.OrderStatusHistories.CountAsync(h => h.PurchaseOrderId == id && h.NewStatus == "DELIVERY_REFUSED"));
        var saved = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.PurchaseOrderId == id);
        Assert.Equal("DELIVERY_REFUSED", saved.OrderStatus); Assert.Equal(receivedAt, saved.ReceivedAt);
        Assert.Equal(stockBefore, await db.Inventories.SumAsync(i => i.QuantityOnHand));
        Assert.Equal(receiptCount, await db.GoodsReceipts.CountAsync());
        Assert.Equal(0, await db.OrderReturns.CountAsync(r => r.PurchaseOrderId == id));
        var batchIds = await db.OrderDetails.Where(l => l.PurchaseOrderId == id).Select(l => l.RequestedProductBatchId).ToListAsync();
        Assert.False(await db.BatchSaleOffers.AnyAsync(o => batchIds.Contains(o.ProductBatchId) && o.IsPublished));
        Assert.False(await db.ProductBatches.AnyAsync(b => batchIds.Contains(b.ProductBatchId) && b.BatchStatus != "ISSUED"));
        Assert.Equal(0, (await Repository().DashboardAsync(buyer, default)).SuccessfulOrders);
        Assert.Equal(0, (await Repository().DashboardAsync(buyer, default)).TotalSpent);
        await Assert.ThrowsAsync<ConflictException>(() => Repository().ConfirmReceiptAsync(buyer, id, default));
        var filtered = await Repository().ListAsync(buyer, new(Status: "DELIVERY_REFUSED"), default);
        Assert.Equal(id, Assert.Single(filtered.Items).Id);
        using var image = await client.GetAsync(view.Images[0].Url);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode); Assert.Equal(Photo, await image.Content.ReadAsByteArrayAsync());
        client.DefaultRequestHeaders.Authorization = Bearer(other);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(view.Images[0].Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/order-delivery-refusals/{id}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(manager, "WAREHOUSE_MANAGER");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(view.Images[0].Url)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(view.Images[0].Url)).StatusCode);
    }

    [ReceivingSqlFact]
    public async Task BeforeReceipt_ConcurrentDifferentReports_OnlyOneCanCloseTheOrder()
    {
        var buyer = await Account("DISTRIBUTOR"); var id = await Order(buyer);
        using var first = await F.ApiAsync(); using var second = await F.ApiAsync();
        first.DefaultRequestHeaders.Authorization = second.DefaultRequestHeaders.Authorization = Bearer(buyer);
        using var one = Form(); using var two = Form();
        var responses = await Task.WhenAll(first.PostAsync($"/api/order-delivery-refusals/{id}", one), second.PostAsync($"/api/order-delivery-refusals/{id}", two));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();
        await using var db = F.Context();
        Assert.Equal(1, await db.OrderDeliveryRefusals.CountAsync(r => r.PurchaseOrderId == id));
        Assert.Equal(1, await db.OrderDeliveryRefusalImages.CountAsync(i => i.OrderDeliveryRefusal.PurchaseOrderId == id));
        Assert.Equal(1, await db.OrderStatusHistories.CountAsync(h => h.PurchaseOrderId == id && h.NewStatus == "DELIVERY_REFUSED"));
        Assert.False((await Repository().GetAsync(buyer, id, default)).CanConfirmReceipt);
    }

    [ReceivingSqlFact]
    public async Task RejectsForeignOrders_WrongRoles_EmptyReason_MissingOrFakeImages_AndUnshippedOrders()
    {
        var buyer = await Account("DISTRIBUTOR"); var other = await Account("DISTRIBUTOR"); var id = await Order(buyer);
        using var client = await F.ApiAsync();
        using (var form = Form()) Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/order-delivery-refusals/{id}", form)).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(buyer, "WAREHOUSE_MANAGER");
        using (var form = Form()) Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/order-delivery-refusals/{id}", form)).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(other);
        using (var form = Form()) Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/order-delivery-refusals/{id}", form)).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(buyer);
        foreach (var bad in new[] { Form(batches: []), Form(batches: [long.MaxValue]), Form(batches: [selectedBatches[0], selectedBatches[0]]), Form(reason: " "), Form(count: 0), Form(count: 6), Form(image: "<script>bad</script>"u8.ToArray()), Form(image: [], name: "empty.png"), Form(image: new byte[10 * 1024 * 1024 + 1]) })
        {
            using (bad) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/order-delivery-refusals/{id}", bad)).StatusCode);
        }
        foreach (var status in new[] { "PENDING", "APPROVED", "PARTIALLY_ISSUED" })
        {
            var pendingId = await Order(buyer, status);
            using var form = Form();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/order-delivery-refusals/{pendingId}", form)).StatusCode);
        }
        var draftId = await Order(buyer, committed: false);
        using var draft = Form();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/order-delivery-refusals/{draftId}", draft)).StatusCode);
        await using var db = F.Context();
        Assert.False(await db.OrderDeliveryRefusals.AnyAsync(r => r.PurchaseOrderId == id));
        Assert.Equal("DISPATCHED", (await db.PurchaseOrders.FindAsync(id))!.OrderStatus);
    }

    [ReceivingSqlFact]
    public async Task RefuseOneLotBeforeReceipt_LeavesOtherLotReceivable_AndCountsOnlyAcceptedValue()
    {
        var buyer = await Account("DISTRIBUTOR"); var id = await Order(buyer);
        var chosen = selectedBatches[0]; var remaining = selectedBatches[1];
        using var client = await F.ApiAsync(); client.DefaultRequestHeaders.Authorization = Bearer(buyer);
        using var form = Form(batches: [chosen]);
        using var response = await client.PostAsync($"/api/order-delivery-refusals/{id}", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<Envelope<DeliveryRefusalView>>())!.Data;
        Assert.Equal(chosen, Assert.Single(report.Lots).BatchId);
        var order = await Repository().GetAsync(buyer, id, default);
        Assert.Equal("PARTIALLY_REFUSED", order.Status); Assert.True(order.CanConfirmReceipt); Assert.Null(order.ReceivedAt);
        Assert.True(order.Lines.Single(l => l.BatchId == chosen).IsDeliveryRefused);
        Assert.False(order.Lines.Single(l => l.BatchId == remaining).IsDeliveryRefused);
        Assert.Equal(0m, (await Repository().DashboardAsync(buyer, default)).TotalSpent);
        using var duplicate = Form(batches: [chosen]);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/order-delivery-refusals/{id}", duplicate)).StatusCode);
        var received = await Repository().ConfirmReceiptAsync(buyer, id, default);
        Assert.Equal("PARTIALLY_REFUSED", received.Status); Assert.NotNull(received.ReceivedAt);
        Assert.False(received.CanConfirmReceipt); Assert.True(received.Lines.Single(l => l.BatchId == chosen).IsDeliveryRefused);
        await Repository().ConfirmReceiptAsync(buyer, id, default);
        Assert.Equal(1500000m, (await Repository().DashboardAsync(buyer, default)).TotalSpent);
        Assert.Equal(1, (await Repository().DashboardAsync(buyer, default)).SuccessfulOrders);
        await using var db = F.Context();
        Assert.Equal(1, await db.OrderDeliveryRefusalLots.CountAsync(l => l.OrderDetail.PurchaseOrderId == id));
        Assert.Equal(1, await db.OrderStatusHistories.CountAsync(h => h.PurchaseOrderId == id && h.ChangeReason != null && h.ChangeReason.StartsWith("Nhà phân phối xác nhận nhận các lô còn lại")));
        var selectedStock = await db.Inventories.Where(i => i.ProductBatchId == chosen).SingleAsync();
        Assert.Equal(0m, selectedStock.QuantityOnHand);
    }

    [ReceivingSqlFact]
    public async Task RefuseSelectedLotsAfterReceipt_StoresSeparateReports_ThenClosesOnlyWhenEveryLotIsRefused()
    {
        var buyer = await Account("DISTRIBUTOR"); var id = await Order(buyer, "DELIVERED");
        var chosen = selectedBatches[0]; var remaining = selectedBatches[1]; var request = Guid.NewGuid();
        using var client = await F.ApiAsync(); client.DefaultRequestHeaders.Authorization = Bearer(buyer);
        var originalReceipt = (await Repository().GetAsync(buyer, id, default)).ReceivedAt;
        using var first = Form(request, batches: [chosen]);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/order-delivery-refusals/{id}", first)).StatusCode);
        Assert.Equal(1500000m, (await Repository().DashboardAsync(buyer, default)).TotalSpent);
        Assert.Equal(1, (await Repository().DashboardAsync(buyer, default)).SuccessfulOrders);
        var partial = await Repository().GetAsync(buyer, id, default);
        Assert.Equal("PARTIALLY_REFUSED", partial.Status); Assert.Equal(originalReceipt, partial.ReceivedAt);
        Assert.False(partial.CanConfirmReceipt); Assert.False(partial.Lines.Single(l => l.BatchId == remaining).IsDeliveryRefused);
        using var changedRequest = Form(request, batches: [remaining]);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/order-delivery-refusals/{id}", changedRequest)).StatusCode);
        using var later = Form(reason: "Lô còn lại cũng bị lỗi", batches: [remaining]);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/order-delivery-refusals/{id}", later)).StatusCode);
        var full = await Repository().GetAsync(buyer, id, default);
        Assert.Equal("DELIVERY_REFUSED", full.Status); Assert.All(full.Lines, l => Assert.True(l.IsDeliveryRefused));
        Assert.Equal(originalReceipt, full.ReceivedAt); Assert.Equal(0m, (await Repository().DashboardAsync(buyer, default)).TotalSpent);
        var reports = (await client.GetFromJsonAsync<Envelope<List<DeliveryRefusalView>>>($"/api/order-delivery-refusals/{id}"))!.Data;
        Assert.Equal(2, reports.Count); Assert.All(reports, r => Assert.Single(r.Lots));
        Assert.Contains(reports, r => r.Reason == "Hàng hỏng khi giao" && r.Lots.Single().BatchId == chosen);
        Assert.Contains(reports, r => r.Reason == "Lô còn lại cũng bị lỗi" && r.Lots.Single().BatchId == remaining);
    }

    [ReceivingSqlFact]
    public async Task ConcurrentReportsForDifferentLots_BothPersistWithoutOverwritingTheirScope()
    {
        var buyer = await Account("DISTRIBUTOR"); var id = await Order(buyer);
        using var first = await F.ApiAsync(); using var second = await F.ApiAsync();
        first.DefaultRequestHeaders.Authorization = second.DefaultRequestHeaders.Authorization = Bearer(buyer);
        using var one = Form(batches: [selectedBatches[0]]); using var two = Form(batches: [selectedBatches[1]]);
        var responses = await Task.WhenAll(first.PostAsync($"/api/order-delivery-refusals/{id}", one), second.PostAsync($"/api/order-delivery-refusals/{id}", two));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var response in responses) response.Dispose();
        await using var db = F.Context();
        Assert.Equal(2, await db.OrderDeliveryRefusals.CountAsync(r => r.PurchaseOrderId == id));
        Assert.Equal(2, await db.OrderDeliveryRefusalLots.CountAsync(l => l.OrderDetail.PurchaseOrderId == id));
        Assert.Equal("DELIVERY_REFUSED", (await db.PurchaseOrders.FindAsync(id))!.OrderStatus);
    }

    [ReceivingSqlFact]
    public async Task ARealLotFromAnotherOrder_CannotBeRefusedOnThisOrder()
    {
        var buyer = await Account("DISTRIBUTOR"); var id = await Order(buyer);
        await Order(buyer);
        using var client = await F.ApiAsync(); client.DefaultRequestHeaders.Authorization = Bearer(buyer);
        using var form = Form(batches: [selectedBatches[0]]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/order-delivery-refusals/{id}", form)).StatusCode);
        Assert.Equal("DISPATCHED", (await Repository().GetAsync(buyer, id, default)).Status);
    }

    [ReceivingSqlFact]
    public async Task FailedSave_RollsBackStatusNotificationsAndEvidenceFiles()
    {
        var buyer = await Account("DISTRIBUTOR"); var id = await Order(buyer);
        await using var db = F.Context();
        var filesBefore = Directory.Exists(F.DeliveryRefusalStoragePath) ? Directory.GetFiles(F.DeliveryRefusalStoragePath).Length : 0;
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_REFUSAL_TEST_FAILURE ON dbo.ORDER_DELIVERY_REFUSAL_IMAGE AFTER INSERT AS THROW 50342,'Test rollback',1;");
        try
        {
            using var client = await F.ApiAsync(); client.DefaultRequestHeaders.Authorization = Bearer(buyer);
            using var form = Form();
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsync($"/api/order-delivery-refusals/{id}", form)).StatusCode);
            Assert.Equal("DISPATCHED", (await db.PurchaseOrders.FindAsync(id))!.OrderStatus);
            Assert.False(await db.OrderDeliveryRefusals.AnyAsync(r => r.PurchaseOrderId == id));
            Assert.False(await db.Notifications.AnyAsync(n => n.NotificationType == "DELIVERY_REFUSAL" && n.RelatedEntityId == id.ToString()));
            Assert.Equal(filesBefore, Directory.Exists(F.DeliveryRefusalStoragePath) ? Directory.GetFiles(F.DeliveryRefusalStoragePath).Length : 0);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_REFUSAL_TEST_FAILURE"); }
    }
}

public sealed class DeliveryRefusalMigrationSqlTests
{
    [ReceivingSqlFact]
    public async Task Migration_PreservesLegacyTablesAndStatusConstraint_AndCanRollbackWhenEmpty()
    {
        var fixture = new ReceivingSqlFixture(useMigrationBaseline: true);
        try
        {
            await fixture.InitializeAsync();
            await using var db = fixture.Context();
            Assert.Contains("20261031135256_AddOrderDeliveryRefusal", await db.Database.GetAppliedMigrationsAsync());
            var original = await db.Database.SqlQueryRaw<string>("SELECT CONVERT(nvarchar(4000),value) AS Value FROM sys.extended_properties WHERE major_id=OBJECT_ID('dbo.PURCHASE_ORDER') AND name='SAW_DeliveryRefusal_PreviousStatusCheck'").SingleAsync();
            var extended = await db.Database.SqlQueryRaw<string>("SELECT definition AS Value FROM sys.check_constraints WHERE name='CK_PURCHASE_ORDER_Status'").SingleAsync();
            Assert.Contains("DELIVERY_REFUSED", extended);
            Assert.Empty(await db.OrderReturns.ToListAsync());
            var migration = db.GetService<IMigrator>();
            await migration.MigrateAsync("20261029150053_AddOrderReturnWorkflow");
            Assert.Equal(original, await db.Database.SqlQueryRaw<string>("SELECT definition AS Value FROM sys.check_constraints WHERE name='CK_PURCHASE_ORDER_Status'").SingleAsync());
            await migration.MigrateAsync();
            Assert.Empty(await db.OrderDeliveryRefusals.ToListAsync());
            Assert.Empty(await db.OrderReturns.ToListAsync());
            var order = new PurchaseOrder { OrderCode = "MIGRATION-REFUSAL", DeliveryAddress = "Test", OrderStatus = "DELIVERY_REFUSED",
                Distributor = new Distributor { AccountId = fixture.ActorId, DistributorCode = "MIGRATION-REFUSAL", DistributorName = "Test", TaxCode = "TEST" } };
            db.PurchaseOrders.Add(order); await db.SaveChangesAsync();
            db.OrderDeliveryRefusals.Add(new OrderDeliveryRefusal { PurchaseOrderId = order.PurchaseOrderId,
                RequestId = Guid.NewGuid(), CreatedByAccountId = fixture.ActorId, Reason = "Keep existing evidence", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => migration.MigrateAsync("20261029150053_AddOrderReturnWorkflow"));
            Assert.Equal(1, await db.OrderDeliveryRefusals.CountAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }
}
