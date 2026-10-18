using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task Request_notifies_managers_only_and_staff_API_exposes_operational_progress_without_negotiations()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        await using var db = F.Context();
        var recipients = await db.Notifications.Where(n => n.RelatedEntityId == a.Summary.Id.ToString() && n.Title == "Có phiếu điều chỉnh tồn cần xử lý")
            .Select(n => n.Account.Role.RoleCode).ToListAsync();
        Assert.NotEmpty(recipients); Assert.All(recipients, role => Assert.Equal("WAREHOUSE_MANAGER", role));
        var sent = await Send(a.Summary.Id, manager); var proposal = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, "ACCEPT", null), sale.Customer, default);
        using var api = await F.ApiAsync();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(F.ActorId, "OPERATION_STAFF"));
        var response = await api.GetAsync("api/operation/stock-adjustments/" + a.Summary.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = json.RootElement.GetProperty("data"); var resolution = detail.GetProperty("resolution");
        Assert.Equal("PENDING_MANAGER", resolution.GetProperty("state").GetString());
        Assert.Equal("PENDING_MANAGER", detail.GetProperty("summary").GetProperty("workState").GetString());
        Assert.Equal(JsonValueKind.Null, resolution.GetProperty("order").ValueKind);
        Assert.Empty(resolution.GetProperty("proposals").EnumerateArray());
        Assert.Equal("CUSTOMER_ACCEPTED", (await Reviews(manager).GetAsync(a.Summary.Id, default)).Request.Resolution!.State);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        var notice = Assert.Single(await db.Notifications.Where(n => n.RelatedEntityId == a.Summary.Id.ToString()
            && n.AccountId == F.ActorId && n.NotificationType == "STOCK_ADJUSTMENT_STAFF_RESULT").ToListAsync());
        Assert.Contains("được duyệt", notice.Message); Assert.DoesNotContain("giá", notice.Message);
    }

    [ReceivingSqlFact]
    public async Task Legacy_misrouted_notices_are_hidden_and_customer_cancellation_does_not_echo_customer_reason()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        var sent = await Send(a.Summary.Id, manager); var proposal = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, "DECLINE", "i dont like"), sale.Customer, default);
        await using var db = F.Context();
        var cancellation = await db.Notifications.SingleAsync(n => n.AccountId == sale.Customer && n.Title == "Đơn hàng đã hủy");
        Assert.Contains("Bạn đã từ chối", cancellation.Message); Assert.DoesNotContain("i dont like", cancellation.Message);
        Assert.Contains("i dont like", (await db.PurchaseOrders.FindAsync(sale.Order))!.CancellationReason);
        Notification Legacy(int recipient) => new() { AccountId = recipient, Title = "Có phiếu điều chỉnh tồn cần xử lý",
            Message = "Nhân viên kho vừa gửi kết quả kiểm đếm.", NotificationType = "STOCK_ADJUSTMENT", Priority = "HIGH",
            RelatedEntityType = "STOCK_ADJUSTMENT", RelatedEntityId = a.Summary.Id.ToString(), CreatedAt = DateTime.UtcNow };
        var staff = Legacy(F.ActorId); var customer = Legacy(sale.Customer); var warehouse = Legacy(manager);
        customer.RelatedEntityType = "DISTRIBUTOR_ORDER"; customer.RelatedEntityId = sale.Order.ToString();
        db.AddRange(staff, customer, warehouse); await db.SaveChangesAsync();
        var inbox = new NotificationInboxRepository(Options(), Accessor);
        Assert.DoesNotContain((await inbox.ListAsync(F.ActorId, 1, 50, default)).Items, n => n.Id == staff.NotificationId);
        Assert.DoesNotContain((await inbox.ListAsync(sale.Customer, 1, 50, default)).Items, n => n.Id == customer.NotificationId);
        Assert.Contains((await inbox.ListAsync(manager, 1, 50, default)).Items, n => n.Id == warehouse.NotificationId);
        await Assert.ThrowsAsync<NotFoundException>(() => inbox.ReadAsync(F.ActorId, staff.NotificationId, default));
        await Assert.ThrowsAsync<NotFoundException>(() => inbox.ReadAsync(sale.Customer, customer.NotificationId, default));
    }

    [ReceivingSqlFact]
    public async Task Disposal_frees_location_capacity_and_rejects_price_changes_even_when_not_publishing()
    {
        var s = await Scenario([10]); var manager = await Manager();
        await using var db = F.Context();
        var location = await db.WarehouseLocations.SingleAsync(l => l.Inventories.Any(i => i.ProductBatchId == s.Batch));
        location.MaxWeightKg = 20;
        db.BatchSaleOffers.Add(new BatchSaleOffer { ProductBatchId = s.Batch, WholeLotPrice = 100000, IsPublished = true });
        await db.SaveChangesAsync();
        var inventory = new WarehouseInventoryRepository(db, new OrderReservationAllocator(Options(), Accessor));
        var before = (await inventory.GetCapacityChartAsync(default)).Locations.Single(l => l.LocationCode == location.LocationCode);
        Assert.Equal(10, before.UsedWeightKg); Assert.Equal(10, before.AvailableWeightKg);
        var a = await Service().CreateAsync(await Request(s.Batch, "DISPOSAL"), F.ActorId, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        var after = (await inventory.GetCapacityChartAsync(default)).Locations.Single(l => l.LocationCode == location.LocationCode);
        Assert.Equal(0, after.UsedWeightKg); Assert.Equal(20, after.AvailableWeightKg);
        var offer = (await inventory.GetSaleOffersAsync(null, 1, 100, default)).Items.Single(l => l.BatchId == s.Batch);
        Assert.Equal("DISPOSED", offer.BatchStatus); Assert.False(offer.IsPublished); Assert.False(offer.CanPublish);
        foreach (var publish in new[] { false, true })
            await Assert.ThrowsAsync<ConflictException>(() => inventory.UpdateSaleOfferAsync(s.Batch, new(50000, publish, offer.PublicationSnapshot), default));
        Assert.Equal(100000, await db.BatchSaleOffers.AsNoTracking().Where(o => o.ProductBatchId == s.Batch).Select(o => o.WholeLotPrice).SingleAsync());
    }
}
