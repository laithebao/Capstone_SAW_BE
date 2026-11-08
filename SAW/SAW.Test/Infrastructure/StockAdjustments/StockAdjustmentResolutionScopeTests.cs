using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task Customer_decline_preserves_full_multi_location_evidence_and_all_count_documents_remain_reviewable()
    {
        var s = await Scenario([40, 60]); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var first = await Service().CreateAsync(await Request(s.Batch, inventory: s.Stocks[0], actual: 30), F.ActorId, default);
        var second = await Service().CreateAsync(await Request(s.Batch, inventory: s.Stocks[1], actual: 40), F.ActorId, default);
        var original = (await ReadEntity(first.Summary.Id)).InventorySnapshotJson;
        var sent = await Send(first.Summary.Id, manager); var proposal = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, "DECLINE", "Không tiếp tục đơn"), sale.Customer, default);
        await using var db = F.Context();
        Assert.Equal(100, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.ReservedQuantity));
        Assert.Equal(original, (await ReadEntity(first.Summary.Id)).InventorySnapshotJson);
        var view = await Reviews(manager).GetAsync(first.Summary.Id, default); Assert.True(view.CanApprove, view.ApprovalLockReason);
        await Reviews(manager).ReviewAsync(first.Summary.Id, Decision(view), manager, default);
        Assert.Equal(70, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal("APPROVED", (await ReadEntity(second.Summary.Id)).AdjustmentStatus);
        Assert.Equal("CANCELLED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderStatus).SingleAsync());
        Assert.False(await db.OrderAmendments.AnyAsync(m => m.OrderDetail.PurchaseOrderId == sale.Order));
    }
    [ReceivingSqlFact]
    public async Task Two_location_count_documents_form_one_customer_proposal_and_finalize_together_before_resuming_order()
    {
        var s = await Scenario([40, 60]); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var first = await Service().CreateAsync(await Request(s.Batch, inventory: s.Stocks[0], actual: 30), F.ActorId, default);
        var second = await Service().CreateAsync(await Request(s.Batch, inventory: s.Stocks[1], actual: 40), F.ActorId, default);
        var sent = await Send(first.Summary.Id, manager); var proposal = sent.Proposals.Single();
        Assert.Equal(70, proposal.ProposedWeightKg); Assert.Equal(2, sent.Counts!.Count);
        var secondary = await Reviews(manager).GetAsync(second.Summary.Id, default); Assert.False(secondary.CanApprove); Assert.False(secondary.CanReject);
        await Assert.ThrowsAsync<ConflictException>(() => Reviews(manager).ReviewAsync(second.Summary.Id, Decision(secondary), manager, default));
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, "ACCEPT", null), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(first.Summary.Id, default); Assert.True(view.CanApprove, view.ApprovalLockReason);
        await Reviews(manager).ReviewAsync(first.Summary.Id, Decision(view), manager, default);
        await using var db = F.Context(); Assert.Equal(70, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(70, await db.OrderDetails.Where(l => l.PurchaseOrderId == sale.Order).Select(l => l.ApprovedWeightKg).SingleAsync());
        Assert.Equal("APPROVED", (await ReadEntity(first.Summary.Id)).AdjustmentStatus); Assert.Equal("APPROVED", (await ReadEntity(second.Summary.Id)).AdjustmentStatus);
        Assert.Equal("RESOLVED", (await Service().GetAsync(second.Summary.Id, default)).Resolution!.State);
        Assert.Single(await db.OrderAmendments.Where(m => m.OrderDetail.PurchaseOrderId == sale.Order).ToListAsync());
    }
    [ReceivingSqlFact]
    public async Task Linked_multi_position_count_proposes_total_batch_weight_and_reuses_unpicked_unique_reservation_rows()
    {
        var s = await Scenario([40, 60]); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        await using var db = F.Context(); var originalIds = await db.InventoryReservations.Where(r => r.OrderDetail.PurchaseOrderId == sale.Order).OrderBy(r => r.InventoryReservationId).Select(r => r.InventoryReservationId).ToListAsync();
        var a = await Service().CreateAsync(await Request(s.Batch, inventory: s.Stocks[0], actual: 30), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        Assert.Equal(90, p.ProposedWeightKg); Assert.Equal(40, a.Summary.SystemQuantity); Assert.Equal(30, a.Summary.ActualQuantity);
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); Assert.True(view.CanApprove, view.ApprovalLockReason);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        Assert.Equal(90, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(originalIds, await db.InventoryReservations.Where(r => r.OrderDetail.PurchaseOrderId == sale.Order).OrderBy(r => r.InventoryReservationId).Select(r => r.InventoryReservationId).ToListAsync());
        Assert.Equal(new[] { 30m, 60m }, await db.InventoryReservations.Where(r => r.OrderDetail.PurchaseOrderId == sale.Order).OrderBy(r => r.InventoryId).Select(r => r.ReservedQuantity).ToArrayAsync());
    }
    [ReceivingSqlFact]
    public async Task Disposal_of_one_line_cancels_whole_order_but_preserves_healthy_lot_stock()
    {
        var bad = await Scenario(); var healthy = await Scenario(); var sale = await SalesOrder(bad.Batch, healthy.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(bad.Batch, "DISPOSAL"), F.ActorId, default);
        Assert.False((await new PickingRepository(Options(), Accessor).GetAsync(sale.Order, default)).Order.CanPick);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        await using var db = F.Context();
        Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == bad.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(100, await db.Inventories.Where(i => i.ProductBatchId == healthy.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == healthy.Batch).SumAsync(i => i.ReservedQuantity));
        Assert.Equal("IN_STOCK", await db.ProductBatches.Where(b => b.ProductBatchId == healthy.Batch).Select(b => b.BatchStatus).SingleAsync());
        Assert.Equal("CANCELLED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderStatus).SingleAsync());
    }
    [ReceivingSqlFact]
    public async Task Cancelled_customer_order_stays_cancelled_after_rejection_and_linked_replacement_count_review()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "DECLINE", "Không mua"), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, "REJECT", "Kiểm lại số lượng chính xác"), manager, default);
        var next = await Service().CreateAsync((await Request(s.Batch, actual: 85)) with { ReplacesStockAdjustmentId = a.Summary.Id }, F.ActorId, default);
        Assert.Equal(sale.Order, next.Resolution!.Order!.Id); Assert.Equal("PENDING_MANAGER", next.Resolution.State);
        view = await Reviews(manager).GetAsync(next.Summary.Id, default); Assert.True(view.CanApprove, view.ApprovalLockReason);
        await Reviews(manager).ReviewAsync(next.Summary.Id, Decision(view), manager, default);
        await using var db = F.Context(); Assert.Equal("CANCELLED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderStatus).SingleAsync());
        Assert.Equal(85, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
    }
    [ReceivingSqlFact]
    public async Task Approved_remainder_uses_proposal_price_and_current_publication_snapshot_without_changing_receiving_weight()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        await using var db = F.Context(); var qc = F.Inspection(s.Batch, "COMPLETED"); qc.QcResult = "PASS"; qc.QualityGrade = "E"; qc.CompletedAt = DateTime.UtcNow.AddSeconds(1); db.Add(qc);
        db.BatchSaleOffers.Add(new BatchSaleOffer { ProductBatchId = s.Batch, WholeLotPrice = 10000000, IsPublished = false, UpdatedAt = DateTime.UtcNow }); await db.SaveChangesAsync();
        var inventory = new WarehouseInventoryRepository(db, new OrderReservationAllocator(Options(), Accessor)); var old = (await inventory.GetSaleOffersAsync(null, 1, 100, default)).Items.Single(o => o.BatchId == s.Batch);
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "DECLINE", "Không mua"), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        var offer = (await inventory.GetSaleOffersAsync(null, 1, 100, default)).Items.Single(o => o.BatchId == s.Batch);
        Assert.False(offer.RequiresRepricing); Assert.True(offer.IsPublished); Assert.Equal(80, offer.VerifiedWeightKg); Assert.Equal(8500000, offer.WholeLotPrice);
        await Assert.ThrowsAsync<ConflictException>(() => inventory.UpdateSaleOfferAsync(s.Batch, new(10000000, true, old.PublicationSnapshot), default));
        var repository = new DistributorOrderRepository(Options(), Accessor); var catalog = await repository.CatalogAsync(sale.Customer, new(Search: offer.BatchCode, PageSize: 100), default);
        Assert.Equal(80, Assert.Single(catalog.Items).WeightKg); Assert.Equal(8500000, catalog.Items.Single().WholeLotPrice);
        var newOrder = await repository.CreateAsync(sale.Customer, new(Guid.NewGuid(), [new(s.Batch, 8500000, 80)], "Fixture address", "0901234567", null, null), default);
        var approved = await inventory.ApproveDistributorOrderAsync(manager, newOrder.Id, default); Assert.Equal("RESERVED", approved.Status);
        Assert.Equal(80, await db.InventoryReservations.Where(r => r.OrderDetail.PurchaseOrderId == newOrder.Id && r.ReservationStatus == "RESERVED").SumAsync(r => r.ReservedQuantity));
        Assert.Equal(80, newOrder.Lines.Single().WeightKg); Assert.Equal(100, await db.ProductBatches.Where(b => b.ProductBatchId == s.Batch).Select(b => b.VerifiedWeightInKg).SingleAsync());
        var picking = new PickingService(new PickingRepository(Options(), Accessor));
        var oldOrder = Assert.Single((await picking.SearchAsync(new(Search: (await db.PurchaseOrders.FindAsync(sale.Order))!.OrderCode, Status: "CANCELLED"), default)).Items);
        Assert.Equal(sale.Order, oldOrder.Id); Assert.False(oldOrder.CanPick); Assert.Equal("CANCELLED", oldOrder.Status);
        Assert.NotEqual(oldOrder.Id, newOrder.Id); Assert.True((await picking.GetAsync(newOrder.Id, default)).Order.CanPick);
    }
}
