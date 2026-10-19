using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsIssues;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task Replacement_cannot_abandon_the_original_inventory_scope()
    {
        var s = await Scenario([40, 60]); await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, inventory: s.Stocks[0], actual: 30), F.ActorId, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, "REJECT", "Kiểm tra lại đúng vị trí"), manager, default);
        var wrong = (await Request(s.Batch, inventory: s.Stocks[1], actual: 40)) with { ReplacesStockAdjustmentId = a.Summary.Id };
        await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(wrong, F.ActorId, default));
        Assert.Equal("REJECTED_HELD", (await Service().GetAsync(a.Summary.Id, default)).Resolution!.State);
    }
    [ReceivingSqlFact]
    public async Task Failed_image_cleanup_retains_a_non_linkable_ticket_and_HTTP_cleanup_runs_outside_inventory_transaction()
    {
        var s = await Scenario(); var storage = Storage(); var service = Service(storage: storage.Object);
        var image = await service.UploadAsync(new SAW.Infrastructure.QrCodes.PngQrCodeRenderer().RenderPng("test cleanup"), F.ActorId, default);
        var attempts = 0;
        storage.Setup(x => x.DeleteAsync(Moq.It.Is<string>(id => id.EndsWith(image.Id.ToString("N"))), Moq.It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) => {
                await using var proof = F.Context(retry: false); proof.Database.SetCommandTimeout(2);
                await using var tx = await proof.Database.BeginTransactionAsync(ct);
                await proof.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX,HOLDLOCK)", ct);
                await tx.RollbackAsync(ct); // Would block/time out if HTTP still held the warehouse write transaction.
                if (++attempts == 1) throw new HttpRequestException("Injected storage cleanup failure");
            });
        await Assert.ThrowsAsync<HttpRequestException>(() => service.RemoveUploadAsync(image.Id, F.ActorId, default));
        await using var db = F.Context(); Assert.True((await db.StockAdjustmentImages.SingleAsync(i => i.StockAdjustmentImageId == image.Id)).DeletePending);
        var request = (await Request(s.Batch)) with { ImageIds = [image.Id] };
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(request, F.ActorId, default));
        await service.RemoveUploadAsync(image.Id, F.ActorId, default);
        Assert.False(await db.StockAdjustmentImages.AnyAsync(i => i.StockAdjustmentImageId == image.Id)); Assert.Equal(2, attempts);
    }
    [ReceivingSqlFact]
    public async Task Explicit_false_report_clearance_requires_review_snapshot_and_restores_original_fulfillment_only_after_verification()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var initial = await State(s.Batch);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, "REJECT", "Báo cáo cần xác minh lại."), manager, default);
        view = await Reviews(manager).GetAsync(a.Summary.Id, default); var r = view.Request.Resolution!;
        await Assert.ThrowsAsync<ConflictException>(() => Resolution().ActAsync(a.Summary.Id, new(Guid.NewGuid(), r.Version, "CLEAR", null, null, "Đã kiểm tra không có hao hụt", ExpectedSnapshot: "stale"), manager, default));
        await Resolution().ActAsync(a.Summary.Id, new(Guid.NewGuid(), r.Version, "CLEAR", null, null, "Đã kiểm tra đầy đủ lô, đủ 100 kg và không hư hỏng.", ExpectedSnapshot: view.ReviewSnapshot), manager, default);
        Assert.Equal(initial, await State(s.Batch)); Assert.Equal("CLEARED", (await Service().GetAsync(a.Summary.Id, default)).Resolution!.State);
        Assert.True((await new PickingRepository(Options(), Accessor).GetAsync(sale.Order, default)).Order.CanPick);
    }
    [ReceivingSqlFact]
    public async Task Manager_cannot_cancel_an_order_from_count_resolution_even_when_remaining_quantity_is_zero()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 0), F.ActorId, default);
        var original = await State(s.Batch);
        await Assert.ThrowsAsync<BadRequestException>(() => Resolution().ActAsync(a.Summary.Id, new(Guid.NewGuid(), a.Resolution!.Version, "CANCEL", null, null, "Cannot cancel from count"), manager, default));
        Assert.Equal(original, await State(s.Batch));
        await using var db = F.Context();
        Assert.Equal("RESERVED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderStatus).SingleAsync());
        Assert.Equal("PENDING", (await ReadEntity(a.Summary.Id)).AdjustmentStatus);
        Assert.False(await db.BatchSaleOffers.AnyAsync(o => o.ProductBatchId == s.Batch));
    }
    [ReceivingSqlFact]
    public async Task Linked_disposal_invalidates_legacy_empty_draft_and_SQL_refuses_unpicked_draft_lines()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        await using var db = F.Context(); var reservation = await db.InventoryReservations.SingleAsync(r => r.OrderDetail.PurchaseOrderId == sale.Order);
        var draft = new GoodsIssue { PurchaseOrderId = sale.Order, OperationAccountId = F.ActorId, IssueCode = "LEGACY-DRAFT-" + Guid.NewGuid().ToString("N"), IssueStatus = "DRAFT", IssuedAt = DateTime.UtcNow };
        db.Add(draft); await db.SaveChangesAsync();
        // Actual trigger requires picking even for draft details. Do not disable it to manufacture an impossible document.
        db.GoodsIssueDetails.Add(new SAW.Domain.Entities.GoodsIssueDetail { GoodsIssueId = draft.GoodsIssueId, InventoryReservationId = reservation.InventoryReservationId, OrderDetailId = reservation.OrderDetailId, InventoryId = reservation.InventoryId, IssuedQuantity = 100, Unit = "kg", WeightInKg = 100 });
        var invalid = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Contains("picked weight", invalid.InnerException!.Message);
        db.ChangeTracker.Clear();
        var a = await Service().CreateAsync(await Request(s.Batch, "DISPOSAL"), F.ActorId, default); var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        Assert.Contains(draft.GoodsIssueId, view.AffectedDraftIssueIds!); Assert.True(view.CanApprove, view.ApprovalLockReason);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        var detail = await new GoodsIssueRepository(Options(), Accessor).GetAsync(draft.GoodsIssueId, default);
        Assert.Equal("CANCELLED", detail.Summary.IssueStatus); Assert.False(detail.CanEdit); Assert.Empty(detail.Lines);
        Assert.Equal("DRAFT", await db.GoodsIssues.AsNoTracking().Where(i => i.GoodsIssueId == draft.GoodsIssueId).Select(i => i.IssueStatus).SingleAsync());
        Assert.True(await db.GoodsIssueInvalidations.AnyAsync(v => v.GoodsIssueId == draft.GoodsIssueId && v.StockAdjustmentId == a.Summary.Id));
    }
    [ReceivingSqlFact]
    public async Task Transfer_SQL_guard_blocks_new_movement_while_adjustment_is_pending_and_after_rejection()
    {
        var s = await Scenario(); var healthy = await Scenario(); await SalesOrder(s.Batch, healthy.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        async Task CheckBlocked(long batch)
        {
            await using var db = F.Context(); var stock = await db.Inventories.SingleAsync(i => i.ProductBatchId == batch); var key = Guid.NewGuid().ToString("N");
            var destination = new WarehouseLocation { LocationCode = "TRANSFER-" + key[..10], ZoneName = "Fixture", LocationStatus = "ACTIVE" }; db.Add(destination); await db.SaveChangesAsync();
            db.StockTransfers.Add(new StockTransfer { ProductBatchId = batch, FromWarehouseLocationId = stock.WarehouseLocationId, ToWarehouseLocationId = destination.WarehouseLocationId,
                OperationAccountId = F.ActorId, TransferCode = "TRANSFER-" + key, SourceTrackingId = "SRC-" + key, DestinationTrackingId = "DST-" + key, Quantity = 1, Unit = "kg", TransferredAt = DateTime.UtcNow });
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(50274, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(failure.InnerException).Number);
        }
        await CheckBlocked(s.Batch); await CheckBlocked(healthy.Batch); var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, "REJECT", "Cần kiểm tra lại."), manager, default); await CheckBlocked(s.Batch); await CheckBlocked(healthy.Batch);
    }
}
