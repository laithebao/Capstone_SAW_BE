using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.DistributorOrders;

public sealed class DistributorBrowserFactAttribute : FactAttribute
{
    public DistributorBrowserFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION"))
            || Environment.GetEnvironmentVariable("SAW_TEST_BROWSER") != "1")
            Skip = "Set SAW_TEST_SQL_CONNECTION and SAW_TEST_BROWSER=1 for the isolated Distributor browser flow.";
    }
}

// Opt-in SQL tests create/drop an isolated GUID database; no changes to the user's warehouse.
public sealed class DistributorOrderSqlTests : IClassFixture<DistributorSqlFixture>
{
    private readonly ReceivingSqlFixture fixture;
    public DistributorOrderSqlTests(DistributorSqlFixture fixture) => this.fixture = fixture.Database;
    private DistributorOrderRepository Repository(TimeProvider? clock = null)
    {
        var options = new DbContextOptionsBuilder<SAW.Infrastructure.Persistence.AppDbContext>()
            .UseSqlServer(fixture.ConnectionString, sql => sql.EnableRetryOnFailure()).Options;
        return new(options, new HttpContextAccessor(), clock);
    }

    private async Task<int> BuyerAsync()
    {
        await using var db = fixture.Context();
        var roleId = await db.Roles.Select(r => r.RoleId).FirstAsync();
        var key = Guid.NewGuid().ToString("N");
        var account = new Account { RoleId = roleId, Username = key, Email = key + "@test.invalid", FullName = "Distributor test",
            PasswordHash = "test-only", AccountStatus = "ACTIVE" };
        db.Distributors.Add(new Distributor { Account = account, DistributorCode = "D-" + key[..20], DistributorName = "Test",
            TaxCode = key[..20], ProfileStatus = "ACTIVE" });
        await db.SaveChangesAsync();
        return account.AccountId;
    }

    private async Task<CatalogLot> LotAsync(decimal price = 1500000m)
    {
        var batch = await fixture.BatchAsync();
        await using var db = fixture.Context();
        var entity = await db.ProductBatches.FindAsync(batch.ProductBatchId);
        entity!.BatchStatus = "IN_STOCK"; entity.QualityGrade = "A";
        var qc = fixture.Inspection(batch.ProductBatchId, "COMPLETED");
        qc.QcResult = "PASS"; qc.QualityGrade = "A"; qc.CompletedAt = qc.StartedAt.AddSeconds(1);
        db.QcInspections.Add(qc);
        var location = new WarehouseLocation { LocationCode = Guid.NewGuid().ToString("N")[..20], ZoneName = "Test storage", LocationStatus = "ACTIVE" };
        db.Inventories.Add(new Inventory { ProductBatchId = batch.ProductBatchId, WarehouseLocation = location, QuantityOnHand = 100, Unit = "kg" });
        db.BatchSaleOffers.Add(new BatchSaleOffer { ProductBatchId = batch.ProductBatchId, WholeLotPrice = price, IsPublished = true, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return new(batch.ProductBatchId, batch.BatchCode, batch.ProductName, "", "", "", "A", batch.HarvestDate, null, 100, price);
    }

    private static CreateDistributorOrderRequest Request(params CatalogLot[] lots) => new(Guid.NewGuid(),
        lots.Select(l => new SelectedLot(l.BatchId, l.WholeLotPrice, l.WeightKg)).ToList(),
        "Đà Nẵng", "0901234567", DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(1), null);

    [ReceivingSqlFact]
    public async Task Dashboard_AggregatesAllOwnedOrders_OnlyDeliveredSpending_AndRecentFour()
    {
        var buyer = await BuyerAsync(); var stranger = await BuyerAsync(); var repo = Repository();
        var empty = await repo.DashboardAsync(buyer, default);
        Assert.Equal(0, empty.PendingOrders); Assert.Equal(0m, empty.TotalSpent); Assert.Empty(empty.RecentOrders);
        await using var db = fixture.Context();
        var ownerId = await db.Distributors.Where(d => d.AccountId == buyer).Select(d => d.DistributorId).SingleAsync();
        var otherId = await db.Distributors.Where(d => d.AccountId == stranger).Select(d => d.DistributorId).SingleAsync();
        var statuses = Enumerable.Repeat("PENDING", 12).Concat(new[] { "DELIVERED", "DELIVERED", "CANCELLED", "APPROVED", "REJECTED", "DISPATCHED" }).ToArray();
        var now = DateTime.UtcNow;
        for (var i = 0; i < statuses.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrder { DistributorId = ownerId, OrderCode = Guid.NewGuid().ToString("N"),
                DeliveryAddress = "Test", OrderStatus = statuses[i], SubtotalAmount = (i + 1) * 100m,
                TotalAmount = (i + 1) * 100m, CreatedAt = now.AddMinutes(i) });
        db.PurchaseOrders.Add(new PurchaseOrder { DistributorId = otherId, OrderCode = Guid.NewGuid().ToString("N"),
            DeliveryAddress = "Other", OrderStatus = "DELIVERED", SubtotalAmount = 999999m, TotalAmount = 999999m, CreatedAt = now.AddDays(1) });
        await db.SaveChangesAsync();
        var dashboard = await repo.DashboardAsync(buyer, default);
        Assert.Equal(12, dashboard.PendingOrders); Assert.Equal(2, dashboard.SuccessfulOrders);
        Assert.Equal(1, dashboard.CancelledOrders); Assert.Equal(2700m, dashboard.TotalSpent);
        Assert.Equal(4, dashboard.RecentOrders.Count);
        Assert.Equal(new[] { "DISPATCHED", "REJECTED", "APPROVED", "CANCELLED" }, dashboard.RecentOrders.Select(o => o.Status));
        Assert.Equal(10, (await repo.ListAsync(buyer, new(), default)).Items.Count);
        var pendingId = await db.PurchaseOrders.Where(o => o.DistributorId == ownerId && o.OrderStatus == "PENDING").Select(o => o.PurchaseOrderId).FirstAsync();
        await repo.CancelAsync(buyer, pendingId, default);
        dashboard = await repo.DashboardAsync(buyer, default);
        Assert.Equal(11, dashboard.PendingOrders); Assert.Equal(2, dashboard.CancelledOrders);
        Assert.Equal(2700m, dashboard.TotalSpent);
    }

    [ReceivingSqlFact]
    public async Task WholeLotOrder_PriceSnapshots_Idempotency_AndNoStockReservation()
    {
        var buyer = await BuyerAsync(); var lot = await LotAsync(); var other = await LotAsync(2000000m);
        var request = Request(lot, other); var repo = Repository();
        var order = await repo.CreateAsync(buyer, request, default);
        var repeat = await repo.CreateAsync(buyer, request, default);
        Assert.Equal(order.Id, repeat.Id); Assert.Equal(3500000m, order.TotalAmount);
        Assert.Equal(2, order.Lines.Count); Assert.True(order.CanCancel); Assert.False(order.CanConfirmReceipt);
        await using var db = fixture.Context();
        var lines = await db.OrderDetails.Where(l => l.PurchaseOrderId == order.Id).ToListAsync();
        Assert.All(lines, l => { Assert.Equal(1m, l.RequestedQuantity); Assert.Equal("Lô", l.Unit); Assert.Equal(100m, l.RequestedWeightKg); });
        Assert.False(await db.InventoryReservations.AnyAsync(r => r.OrderDetail.PurchaseOrderId == order.Id));
        var offer = await db.BatchSaleOffers.FindAsync(lot.BatchId); offer!.WholeLotPrice += 10;
        await db.SaveChangesAsync();
        Assert.Equal(1500000m, (await repo.GetAsync(buyer, order.Id, default)).Lines.First().WholeLotPrice);
        await Assert.ThrowsAsync<ConflictException>(() => repo.CreateAsync(buyer, Request(lot), default));
    }

    [ReceivingSqlFact]
    public async Task OrderWithoutRequestedReceiptDate_PersistsAndReturnsNull()
    {
        var buyer = await BuyerAsync(); var lot = await LotAsync(); var repo = Repository();
        var order = await repo.CreateAsync(buyer, Request(lot) with { ExpectedDeliveryDate = null }, default);
        Assert.Null(order.ExpectedDeliveryDate);
        Assert.Null((await repo.ListAsync(buyer, new(), default)).Items.Single().ExpectedDeliveryDate);
        await using var db = fixture.Context();
        Assert.Null((await db.PurchaseOrders.FindAsync(order.Id))!.ExpectedDeliveryDate);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [ReceivingSqlFact]
    public async Task OrderNumber_UsesVietnamDate_AndResetsOnNextDay()
    {
        var buyer = await BuyerAsync(); var lot = await LotAsync();
        var firstRepo = Repository(new FixedClock(new DateTimeOffset(2031, 1, 1, 16, 59, 0, TimeSpan.Zero)));
        var first = await firstRepo.CreateAsync(buyer, Request(lot), default);
        var second = await firstRepo.CreateAsync(buyer, Request(lot), default);
        Assert.Equal("PO-20310101-01", first.OrderCode);
        Assert.Equal("PO-20310101-02", second.OrderCode);
        var nextRepo = Repository(new FixedClock(new DateTimeOffset(2031, 1, 1, 17, 1, 0, TimeSpan.Zero)));
        Assert.Equal("PO-20310102-01", (await nextRepo.CreateAsync(buyer, Request(lot), default)).OrderCode);
    }

    [ReceivingSqlFact]
    public async Task ConcurrentCreation_HasDistinctSequentialCodes_AndSameRequestDoesNotConsumeAnotherNumber()
    {
        var buyer = await BuyerAsync(); var buyer2 = await BuyerAsync(); var lot = await LotAsync();
        var clock = new FixedClock(new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = Repository(clock);
        var created = await Task.WhenAll(repo.CreateAsync(buyer, Request(lot), default), repo.CreateAsync(buyer2, Request(lot), default));
        Assert.Equal(new[] { "PO-20320101-01", "PO-20320101-02" }, created.Select(o => o.OrderCode).OrderBy(c => c).ToArray());
        var request = Request(lot);
        var repeated = await Task.WhenAll(repo.CreateAsync(buyer, request, default), repo.CreateAsync(buyer, request, default));
        Assert.Equal(repeated[0].Id, repeated[1].Id);
        Assert.Equal("PO-20320101-03", repeated[0].OrderCode);
        await using var db = fixture.Context();
        Assert.Equal(3, (await db.PurchaseOrderDailyCounters.FindAsync(new DateOnly(2032, 1, 1)))!.LastNumber);
    }

    [ReceivingSqlFact]
    public async Task LotDetail_IsAvailableToCatalogBuyers_OrOwnOrderOwner_AndNeverLeaksUnlistedLots()
    {
        var buyer = await BuyerAsync(); var stranger = await BuyerAsync(); var lot = await LotAsync(); var repo = Repository();
        var detail = await repo.LotAsync(buyer, lot.BatchId, default);
        Assert.True(detail.CanPurchase); Assert.Equal(100m, detail.WeightKg); Assert.Equal(1500000m, detail.WholeLotPrice);
        await repo.CreateAsync(buyer, Request(lot), default);
        await using (var db = fixture.Context())
        {
            var offer = await db.BatchSaleOffers.FindAsync(lot.BatchId); offer!.IsPublished = false;
            await db.SaveChangesAsync();
        }
        Assert.False((await repo.LotAsync(buyer, lot.BatchId, default)).CanPurchase);
        await Assert.ThrowsAsync<NotFoundException>(() => repo.LotAsync(stranger, lot.BatchId, default));
        await Assert.ThrowsAsync<NotFoundException>(() => repo.LotAsync(buyer, long.MaxValue, default));
    }

    [ReceivingSqlFact]
    public async Task LotDetail_UsesTheStandardVersionLinkedToItsLatestInspection_NotTheNewestPublishedVersion()
    {
        var buyer = await BuyerAsync(); var lot = await LotAsync(); var repo = Repository();
        string originalName;
        int originalVersion;
        await using (var db = fixture.Context())
        {
            var applied = await db.InspectionStandardVersions.Include(v => v.InspectionStandardSet)
                .SingleAsync(v => v.InspectionStandardVersionId == fixture.VersionId);
            originalName = applied.InspectionStandardSet.StandardName;
            originalVersion = applied.VersionNo;
            db.InspectionStandardVersions.Add(new InspectionStandardVersion { InspectionStandardSetId = applied.InspectionStandardSetId,
                VersionNo = applied.VersionNo + 100, VersionStatus = "PUBLISHED", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var detail = await repo.LotAsync(buyer, lot.BatchId, default);
        Assert.Equal(originalName, detail.InspectionStandardName);
        Assert.Equal(originalVersion, detail.InspectionStandardVersionNo);
        // A new inspection using a different set/version must change all displayed QC metadata together.
        await using (var db = fixture.Context())
        {
            var set = new InspectionStandardSet { CropTypeId = fixture.CropTypeId, StandardCode = Guid.NewGuid().ToString("N"),
                StandardName = "Different applied standard", IsActive = true, CreatedAt = DateTime.UtcNow };
            var version = new InspectionStandardVersion { InspectionStandardSet = set, VersionNo = 7, VersionStatus = "PUBLISHED", CreatedAt = DateTime.UtcNow };
            var inspection = fixture.Inspection(lot.BatchId, "COMPLETED");
            inspection.InspectionStandardVersionId = 0; inspection.InspectionStandardVersion = version;
            inspection.StartedAt = DateTime.UtcNow.AddMinutes(1); inspection.CompletedAt = inspection.StartedAt.AddMinutes(1);
            inspection.QcResult = "PASS"; inspection.QualityGrade = "A";
            db.QcInspections.Add(inspection);
            await db.SaveChangesAsync();
        }
        var latest = await repo.LotAsync(buyer, lot.BatchId, default);
        Assert.Equal("Different applied standard", latest.InspectionStandardName);
        Assert.Equal(7, latest.InspectionStandardVersionNo);
    }

    [ReceivingSqlFact]
    public async Task Catalog_ExcludesUnpublishedExpiredReservedPartialAndFailedQcLots()
    {
        var buyer = await BuyerAsync(); var lots = new List<CatalogLot>();
        for (var i = 0; i < 6; i++) lots.Add(await LotAsync());
        await using (var db = fixture.Context())
        {
            (await db.BatchSaleOffers.FindAsync(lots[0].BatchId))!.IsPublished = false;
            (await db.ProductBatches.FindAsync(lots[1].BatchId))!.ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(-1);
            (await db.Inventories.SingleAsync(i => i.ProductBatchId == lots[2].BatchId)).ReservedQuantity = 100;
            (await db.Inventories.SingleAsync(i => i.ProductBatchId == lots[3].BatchId)).QuantityOnHand = 50;
            var latest = fixture.Inspection(lots[4].BatchId, "DRAFT"); latest.StartedAt = DateTime.UtcNow.AddMinutes(1); db.QcInspections.Add(latest);
            await db.SaveChangesAsync();
        }
        var page = await Repository().CatalogAsync(buyer, new(PageSize: 50), default);
        Assert.Contains(page.Items, l => l.BatchId == lots[5].BatchId);
        Assert.All(lots.Take(5), l => Assert.DoesNotContain(page.Items, row => row.BatchId == l.BatchId));
        foreach (var lot in lots.Take(5)) await Assert.ThrowsAsync<ConflictException>(() => Repository().CreateAsync(buyer, Request(lot), default));
    }

    [ReceivingSqlFact]
    public async Task Ownership_IsEnforcedOnListDetailCancelAndReceive()
    {
        var buyer = await BuyerAsync(); var stranger = await BuyerAsync();
        var repo = Repository(); var order = await repo.CreateAsync(buyer, Request(await LotAsync()), default);
        Assert.Empty((await repo.ListAsync(stranger, new(), default)).Items);
        await Assert.ThrowsAsync<NotFoundException>(() => repo.GetAsync(stranger, order.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => repo.CancelAsync(stranger, order.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => repo.ConfirmReceiptAsync(stranger, order.Id, default));
    }

    [ReceivingSqlFact]
    public async Task CancelPending_IsIdempotent_AndRejectsAlreadyApprovedOrder()
    {
        var buyer = await BuyerAsync(); var repo = Repository();
        var order = await repo.CreateAsync(buyer, Request(await LotAsync()), default);
        var cancelled = await repo.CancelAsync(buyer, order.Id, default);
        Assert.Equal("CANCELLED", cancelled.Status); Assert.False(cancelled.CanCancel);
        await repo.CancelAsync(buyer, order.Id, default);
        Assert.Single((await repo.GetAsync(buyer, order.Id, default)).History, h => h.NewStatus == "CANCELLED");
        var next = await repo.CreateAsync(buyer, Request(await LotAsync()), default);
        await using (var db = fixture.Context())
        {
            var entity = await db.PurchaseOrders.FindAsync(next.Id); entity!.OrderStatus = "APPROVED"; entity.ApprovedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ConflictException>(() => repo.CancelAsync(buyer, next.Id, default));
    }

    [ReceivingSqlFact]
    public async Task Receive_RequiresFullCommittedIssue_AndRecordsBuyerOnce()
    {
        var buyer = await BuyerAsync(); var lot = await LotAsync(); var repo = Repository();
        var order = await repo.CreateAsync(buyer, Request(lot), default);
        await Assert.ThrowsAsync<ConflictException>(() => repo.ConfirmReceiptAsync(buyer, order.Id, default));
        await using (var db = fixture.Context())
        {
            var entity = await db.PurchaseOrders.Include(o => o.OrderDetails).SingleAsync(o => o.PurchaseOrderId == order.Id);
            entity.OrderStatus = "DISPATCHED";
            var line = entity.OrderDetails.Single(); line.ApprovedQuantity = 1; line.ApprovedWeightKg = 100;
            var stock = await db.Inventories.SingleAsync(i => i.ProductBatchId == lot.BatchId);
            var reservation = new InventoryReservation { OrderDetailId = line.OrderDetailId, InventoryId = stock.InventoryId,
                ReservedQuantity = 100, PickedQuantity = 100, ReservationStatus = "ISSUED", ReservedAt = DateTime.UtcNow };
            var issue = new GoodsIssue { IssueCode = Guid.NewGuid().ToString("N"), PurchaseOrderId = order.Id,
                OperationAccountId = fixture.ActorId, IssueStatus = "DRAFT", IssuedAt = DateTime.UtcNow };
            db.GoodsIssueDetails.Add(new GoodsIssueDetail { GoodsIssue = issue, OrderDetailId = line.OrderDetailId,
                InventoryReservation = reservation, InventoryId = stock.InventoryId, IssuedQuantity = 1, Unit = "Lô", WeightInKg = 100 });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ConflictException>(() => repo.ConfirmReceiptAsync(buyer, order.Id, default));
        await using (var db = fixture.Context())
        {
            var issue = await db.GoodsIssues.SingleAsync(i => i.PurchaseOrderId == order.Id); issue.IssueStatus = "COMMITTED"; issue.CommittedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var received = await repo.ConfirmReceiptAsync(buyer, order.Id, default);
        Assert.Equal("DELIVERED", received.Status); Assert.NotNull(received.ReceivedAt); Assert.False(received.CanConfirmReceipt);
        await repo.ConfirmReceiptAsync(buyer, order.Id, default);
        Assert.Single((await repo.GetAsync(buyer, order.Id, default)).History, h => h.NewStatus == "DELIVERED");
        await using var verify = fixture.Context();
        Assert.Equal(buyer, (await verify.PurchaseOrders.FindAsync(order.Id))!.ReceivedByAccountId);
    }

    [ReceivingSqlFact]
    public async Task HttpEndpoints_RequireDistributorRole_AndDeserializeCreateAndQuery()
    {
        var buyer = await BuyerAsync(); var lot = await LotAsync();
        using var api = await fixture.ApiAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/distributor/orders")).StatusCode);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(buyer, "SUPPLIER"));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/distributor/orders")).StatusCode);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(buyer, "DISTRIBUTOR"));
        var list = await api.GetAsync("/api/distributor/orders?page=1&pageSize=10"); Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var created = await api.PostAsJsonAsync("/api/distributor/orders", Request(lot)); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsJsonAsync("/api/distributor/orders", Request(lot) with { ExpectedDeliveryDate = null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/distributor/orders", Request(lot) with { ExpectedDeliveryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(-1) })).StatusCode);
        var lotDetail = await api.GetAsync($"/api/distributor/orders/catalog/{lot.BatchId}"); Assert.Equal(HttpStatusCode.OK, lotDetail.StatusCode);
        foreach (var phone in new[] { "090123456", "090123456a", "+84901234567" })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/distributor/orders", Request(lot) with { ContactPhone = phone })).StatusCode);
        var invalid = await api.GetAsync("/api/distributor/orders?pageSize=100"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [DistributorBrowserFact]
    public async Task Browser_Catalog_Create_List_Detail_Cancel_AndRoleGuards()
    {
        var buyer = await BuyerAsync(); var lot = await LotAsync();
        using var api = await fixture.ApiAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Capstone_SAW_FE"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new System.Diagnostics.ProcessStartInfo("node") { WorkingDirectory = Path.Combine(root.FullName, "Capstone_SAW_FE"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("tests/distributor-orders-browser.mjs");
        start.Environment["SAW_TEST_API_URL"] = api.BaseAddress!.ToString();
        start.Environment["SAW_TEST_BATCH_CODE"] = lot.BatchCode;
        start.Environment["SAW_TEST_TOKEN"] = Token(buyer, "DISTRIBUTOR");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        var list = await Repository().ListAsync(buyer, new(), default);
        var order = Assert.Single(list.Items);
        Assert.Equal("CANCELLED", order.Status);
        Assert.Equal(1500000m, order.TotalAmount);
    }

    private static string Token(int account, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "uc28-test", "uc28-test", [new Claim("account_id", account.ToString()), new Claim(ClaimTypes.Role, role)],
        expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256)));
}
