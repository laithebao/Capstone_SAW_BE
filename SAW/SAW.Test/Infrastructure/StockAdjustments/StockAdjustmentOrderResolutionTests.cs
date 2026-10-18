using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.Picking;
using SAW.Application.Features.GoodsIssues;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    private StockAdjustmentRepository Resolution() => new(Options(), Accessor, Storage().Object);
    private async Task<(long Order, int Customer)> SalesOrder(long batch, long? otherBatch = null)
    {
        var id = await Order(batch);
        await using var db = F.Context();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.RoleCode == "DISTRIBUTOR");
        if (role == null) { role = new Role { RoleCode = "DISTRIBUTOR", RoleName = "Fixture distributor", IsActive = true }; db.Add(role); }
        var qc = F.Inspection(batch, "COMPLETED"); qc.StartedAt = DateTime.UtcNow.AddMinutes(-1); qc.CompletedAt = DateTime.UtcNow.AddSeconds(-1); qc.QcResult = "PASS"; qc.QualityGrade = "E"; db.Add(qc);
        var key = Guid.NewGuid().ToString("N");
        var account = new Account { Role = role, Username = key, Email = key + "@test.invalid", FullName = "Resolution customer", PasswordHash = "unused", AccountStatus = "ACTIVE" };
        var distributor = new Distributor { Account = account, DistributorCode = "R-" + key[..15], DistributorName = "Resolution buyer", ProfileStatus = "ACTIVE", TaxCode = key[..10] };
        db.Add(distributor); await db.SaveChangesAsync();
        var order = await db.PurchaseOrders.Include(o => o.OrderDetails).SingleAsync(o => o.PurchaseOrderId == id);
        if (otherBatch is { } second) order.OrderDetails.Add(new OrderDetail { RequestedProductBatchId = second, CropTypeId = F.CropTypeId, RequestedQuantity = 1, ApprovedQuantity = 1, Unit = "Lô", RequestedWeightKg = 100, ApprovedWeightKg = 100, UnitPrice = 10000000 });
        order.DistributorId = distributor.DistributorId; order.SubtotalAmount = order.OrderDetails.Count * 10000000; order.TotalAmount = order.SubtotalAmount;
        foreach (var line in order.OrderDetails) line.UnitPrice = 10000000;
        await db.SaveChangesAsync();
        Assert.True(await new OrderReservationAllocator(Options(), Accessor).TryReserveAsync(id, F.ActorId, default));
        return (id, account.AccountId);
    }
    private async Task<AdjustmentResolutionDetail> Send(long adjustmentId, int manager, decimal price = 8500000)
    {
        var r = (await Service().GetAsync(adjustmentId, default)).Resolution!;
        await Resolution().ActAsync(adjustmentId, new(Guid.NewGuid(), r.Version, "SEND", price, "Đã kiểm tra phần lô còn lại đủ điều kiện giao.", null, ExpectedSnapshot: (await Reviews(manager).GetAsync(adjustmentId, default)).ReviewSnapshot), manager, default);
        return (await Service().GetAsync(adjustmentId, default)).Resolution!;
    }
    [ReceivingSqlFact]
    public async Task Disposal_cancels_entire_unpicked_order_releases_holds_and_disposes_all_batch_positions_atomically()
    {
        var s = await Scenario([40, 60]); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var before = await State(s.Batch);
        var a = await Service().CreateAsync(await Request(s.Batch, "DISPOSAL"), F.ActorId, default);
        Assert.Equal(before, await State(s.Batch)); Assert.Equal(sale.Order, a.Resolution!.Order!.Id);
        var picking = new PickingService(new PickingRepository(Options(), Accessor)); Assert.False((await picking.GetAsync(sale.Order, default)).Order.CanPick);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); Assert.True(view.CanApprove, view.ApprovalLockReason);
        var command = Decision(view); await Reviews(manager).ReviewAsync(a.Summary.Id, command, manager, default);
        await using var db = F.Context();
        Assert.Equal("CANCELLED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderStatus).SingleAsync());
        Assert.All(await db.Inventories.Where(i => i.ProductBatchId == s.Batch).ToListAsync(), i => { Assert.Equal(0, i.QuantityOnHand); Assert.Equal(0, i.ReservedQuantity); });
        Assert.All(await db.InventoryReservations.Where(r => r.OrderDetail.PurchaseOrderId == sale.Order).ToListAsync(), r => Assert.Equal("RELEASED", r.ReservationStatus));
        Assert.Equal("DISPOSED", await db.ProductBatches.Where(b => b.ProductBatchId == s.Batch).Select(b => b.BatchStatus).SingleAsync());
        var count = await db.Notifications.CountAsync(); var state = await State(s.Batch);
        await Reviews(manager).ReviewAsync(a.Summary.Id, command, manager, default);
        Assert.Equal(state, await State(s.Batch)); Assert.Equal(count, await db.Notifications.CountAsync());
    }
    [ReceivingSqlFact]
    public async Task Customer_decline_cancels_before_count_review_and_owned_release_snapshot_remains_reviewable()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        var original = (await ReadEntity(a.Summary.Id)).InventorySnapshotJson;
        var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        var response = new AdjustmentResponseRequest(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "DECLINE", "Không tiếp tục mua.");
        await Resolution().RespondAsync(sale.Order, response, sale.Customer, default);
        await Resolution().RespondAsync(sale.Order, response, sale.Customer, default);
        await using var db = F.Context();
        Assert.Equal(100, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.ReservedQuantity));
        Assert.Equal("PENDING", (await ReadEntity(a.Summary.Id)).AdjustmentStatus);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); Assert.True(view.CanApprove, view.ApprovalLockReason);
        var decision = Decision(view);
        await Reviews(manager).ReviewAsync(a.Summary.Id, decision, manager, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, decision, manager, default);
        var offer = await db.BatchSaleOffers.SingleAsync(o => o.ProductBatchId == s.Batch);
        Assert.Equal(8500000, offer.WholeLotPrice); Assert.Equal(a.Summary.Id, offer.RepricedForAdjustmentId); Assert.False(offer.IsPublished);
        Assert.Equal(80, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal("CANCELLED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderStatus).SingleAsync());
        Assert.Equal(10000000, await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.TotalAmount).SingleAsync());
        Assert.Equal(original, (await ReadEntity(a.Summary.Id)).InventorySnapshotJson); Assert.Empty(await db.OrderAmendments.Where(m => m.OrderDetail.PurchaseOrderId == sale.Order).ToListAsync());
    }
    [ReceivingSqlFact]
    public async Task Customer_accept_then_manager_finalizes_same_order_80kg_price_8500000_and_can_pick_issue_receive()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager(); var original = await State(s.Batch);
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default);
        Assert.Equal(original, await State(s.Batch));
        await using (var beforeDb = F.Context()) {
            var unchanged = await beforeDb.OrderDetails.Where(l => l.PurchaseOrderId == sale.Order).Select(l => new { l.ApprovedWeightKg, l.UnitPrice, l.PurchaseOrder.TotalAmount }).SingleAsync();
            Assert.Equal(100, unchanged.ApprovedWeightKg); Assert.Equal(10000000, unchanged.UnitPrice); Assert.Equal(10000000, unchanged.TotalAmount);
            Assert.False(await beforeDb.OrderAmendments.AnyAsync(m => m.OrderDetail.PurchaseOrderId == sale.Order));
        }
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); Assert.True(view.CanApprove, view.ApprovalLockReason);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        await using var db = F.Context(); var line = await db.OrderDetails.SingleAsync(l => l.PurchaseOrderId == sale.Order);
        Assert.Equal(100, line.RequestedWeightKg); Assert.Equal(80, line.ApprovedWeightKg); Assert.Equal(8500000, line.UnitPrice); Assert.Equal(80, line.ReservedWeightKg);
        Assert.Equal(80, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        var amendment = await db.OrderAmendments.SingleAsync(m => m.OrderDetailId == line.OrderDetailId); Assert.Equal(10000000, amendment.PreviousWholeLotPrice);
        var picking = new PickingService(new PickingRepository(Options(), Accessor)); var detail = await picking.GetAsync(sale.Order, default);
        Assert.True(detail.Order.CanPick, detail.Order.PickingLockReason); var allocation = detail.Lines.Single().Reservations.Single();
        await picking.ConfirmAsync(sale.Order, new(Guid.NewGuid(), allocation.Id, line.OrderDetailId, allocation.InventoryId, s.Batch, 80, 0, "Soạn nguyên lô sau điều chỉnh"), F.ActorId, default);
        Assert.Equal(80, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        var issues = new GoodsIssueService(new GoodsIssueRepository(Options(), Accessor));
        var draft = await issues.CreateAsync(new(Guid.NewGuid(), sale.Order, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)), null, null, [new(allocation.Id, 80)]), F.ActorId, default);
        await issues.ConfirmAsync(draft.Summary.Id, new(Guid.NewGuid(), draft.Snapshot), F.ActorId, default);
        var received = await new DistributorOrderRepository(Options(), Accessor).ConfirmReceiptAsync(sale.Customer, sale.Order, default);
        Assert.Equal("DELIVERED", received.Status); Assert.Equal(80, received.Lines.Single().WeightKg); Assert.Equal(100, received.Lines.Single().OriginalWeightKg); Assert.Equal(10000000, received.Lines.Single().OriginalWholeLotPrice);
        Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        var offer = await db.BatchSaleOffers.SingleAsync(o => o.ProductBatchId == s.Batch);
        Assert.Equal(8500000, offer.WholeLotPrice);
        // Legacy offers could still hold the price from before the amendment. GET uses the sold order.
        offer.WholeLotPrice = 10000000; await db.SaveChangesAsync();
        var listing = await new WarehouseInventoryRepository(db, new OrderReservationAllocator(Options(), Accessor)).GetSaleOffersAsync(null, 1, 100, default);
        Assert.Equal(8500000, listing.Items.Single(o => o.BatchId == s.Batch).WholeLotPrice);
        Assert.Equal(10000000, await db.BatchSaleOffers.AsNoTracking().Where(o => o.ProductBatchId == s.Batch).Select(o => o.WholeLotPrice).SingleAsync());
        Assert.Equal(100, await db.ProductBatches.Where(b => b.ProductBatchId == s.Batch).Select(b => b.VerifiedWeightInKg).SingleAsync());
    }
    [ReceivingSqlFact]
    public async Task Rejected_request_keeps_order_hold_and_replacement_is_linked_without_modifying_original_evidence()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var original = await State(s.Batch);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, "REJECT", "Kiểm tra lại trước khi kết luận."), manager, default);
        Assert.Equal(original, await State(s.Batch));
        Assert.False((await new PickingRepository(Options(), Accessor).GetAsync(sale.Order, default)).Order.CanPick);
        await Assert.ThrowsAsync<ConflictException>(async () => await Service().CreateAsync(await Request(s.Batch, actual: 85), F.ActorId, default));
        var replacement = await Service().CreateAsync((await Request(s.Batch, actual: 85)) with { ReplacesStockAdjustmentId = a.Summary.Id }, F.ActorId, default);
        Assert.Equal(a.Summary.Id, replacement.Resolution!.ReplacesStockAdjustmentId);
        Assert.Equal("REPLACED", (await Service().GetAsync(a.Summary.Id, default)).Resolution!.State);
    }
}
