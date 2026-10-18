using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    private static async Task<Exception?> Outcome(Func<Task> action) { try { await action(); return null; } catch (Exception e) { return e; } }

    [ReceivingSqlFact]
    public async Task Concurrent_opposite_customer_responses_commit_only_one_decision_on_SQL_Server()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        var result = await Task.WhenAll(Outcome(() => Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default)),
            Outcome(() => Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "DECLINE", "Không mua"), sale.Customer, default)));
        Assert.Single(result, e => e == null); Assert.IsType<ConflictException>(Assert.Single(result, e => e != null));
        await using var db = F.Context(); var current = (await Service().GetAsync(a.Summary.Id, default)).Resolution!;
        Assert.Contains(current.State, new[] { "CUSTOMER_ACCEPTED", "CUSTOMER_DECLINED" }); Assert.Equal(100, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(1, await db.AuditLogs.CountAsync(l => l.EntityName == "STOCK_ADJUSTMENT" && l.EntityId == a.Summary.Id.ToString() && l.ActionType.StartsWith("RESOLUTION_CUSTOMER_")));
    }
    [ReceivingSqlFact]
    public async Task Concurrent_manager_final_reviews_resize_exactly_once_on_SQL_Server()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); var command = Decision(view);
        var results = await Task.WhenAll(Outcome(() => Reviews(manager).ReviewAsync(a.Summary.Id, command, manager, default)),
            Outcome(() => Reviews(manager).ReviewAsync(a.Summary.Id, command with { RequestId = Guid.NewGuid() }, manager, default)));
        Assert.Single(results, e => e == null); Assert.IsType<ConflictException>(Assert.Single(results, e => e != null));
        await using var db = F.Context(); Assert.Equal(80, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
        Assert.Single(await db.OrderAmendments.Where(m => m.OrderDetail.PurchaseOrderId == sale.Order).ToListAsync());
        Assert.Equal(80, await db.InventoryReservations.Where(r => r.OrderDetail.PurchaseOrderId == sale.Order).SumAsync(r => r.ReservedQuantity));
        Assert.Equal(1, await db.AuditLogs.CountAsync(l => l.EntityName == "STOCK_ADJUSTMENT" && l.EntityId == a.Summary.Id.ToString() && l.ActionType == "REVIEW_STOCK_ADJUSTMENT"));
    }
    [ReceivingSqlFact]
    public async Task Disposal_failure_after_cancel_and_release_rolls_back_all_order_stock_and_notification_changes()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, "DISPOSAL"), F.ActorId, default); var view = await Reviews(manager).GetAsync(a.Summary.Id, default); var initial = await State(s.Batch);
        await using var db = F.Context(); var notices = await db.Notifications.CountAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => Reviews(manager, new FailResolutionAudit("REVIEW_STOCK_ADJUSTMENT")).ReviewAsync(a.Summary.Id, Decision(view), manager, default));
        Assert.Equal(initial, await State(s.Batch)); Assert.Equal(notices, await db.Notifications.CountAsync());
        Assert.Equal("RESERVED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderStatus).SingleAsync());
        Assert.Equal("PENDING", (await ReadEntity(a.Summary.Id)).AdjustmentStatus);
    }
    [ReceivingSqlFact]
    public async Task External_change_at_another_inventory_position_blocks_the_linked_review_scope()
    {
        var s = await Scenario([40, 60]); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, inventory: s.Stocks[0], actual: 30), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await using var db = F.Context(); await db.Inventories.Where(i => i.InventoryId == s.Stocks[1]).ExecuteUpdateAsync(x => x.SetProperty(i => i.QuantityOnHand, 61).SetProperty(i => i.LastUpdatedAt, DateTime.UtcNow));
        await Assert.ThrowsAsync<ConflictException>(() => Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default));
        Assert.Equal("PENDING_CUSTOMER", (await Service().GetAsync(a.Summary.Id, default)).Resolution!.State);
    }
}
