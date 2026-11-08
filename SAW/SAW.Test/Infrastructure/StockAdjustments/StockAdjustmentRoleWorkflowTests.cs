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
    public async Task Staff_API_exposes_operational_progress_without_negotiations()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
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
