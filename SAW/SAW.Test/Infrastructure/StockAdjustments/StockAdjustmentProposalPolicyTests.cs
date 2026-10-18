using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task Customer_view_never_exposes_internal_review_reason_or_staff_resolution_metadata()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); await Send(a.Summary.Id, manager);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, "REJECT", "INTERNAL-DO-NOT-DISCLOSE"), manager, default);
        var customer = await Resolution().ForOrderAsync(sale.Order, sale.Customer, default);
        Assert.DoesNotContain("INTERNAL-DO-NOT-DISCLOSE", System.Text.Json.JsonSerializer.Serialize(customer));
        Assert.Equal(a.Summary.BatchCode, customer.Single().BatchCode); Assert.Equal(a.Summary.ProductName, customer.Single().ProductName);
        Assert.Null(customer.Single().Counts); Assert.Null(customer.Single().UpdatedBy); Assert.Null(customer.Single().ResolutionReason);
        Assert.Equal("INTERNAL-DO-NOT-DISCLOSE", (await Service().GetAsync(a.Summary.Id, default)).ReviewNote);
    }
    [ReceivingSqlFact]
    public async Task Proposal_revisions_are_immutable_old_customer_responses_conflict_and_replays_do_not_duplicate_notifications()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        var sent = await Send(a.Summary.Id, manager); var old = sent.Proposals.Single();
        await using var db = F.Context();
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.StockAdjustmentProposals.Where(p => p.StockAdjustmentProposalId == old.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ProposedWholeLotPrice, 1)));
        await Resolution().ActAsync(a.Summary.Id, new(Guid.NewGuid(), sent.Version, "WITHDRAW", null, null, "Cần thay giá sau kiểm tra.", old.Id), manager, default);
        await Assert.ThrowsAsync<ConflictException>(() => Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, old.Id, old.Revision, "ACCEPT", null), sale.Customer, default));
        var fresh = await Send(a.Summary.Id, manager, 8400000); var proposal = fresh.Proposals.First(); Assert.Equal(2, proposal.Revision);
        var command = new AdjustmentResponseRequest(Guid.NewGuid(), fresh.Version, proposal.Id, proposal.Revision, "ACCEPT", null);
        await Resolution().RespondAsync(sale.Order, command, sale.Customer, default); var notificationCount = await db.Notifications.CountAsync();
        await Resolution().RespondAsync(sale.Order, command, sale.Customer, default); Assert.Equal(notificationCount, await db.Notifications.CountAsync());
        var audit = await db.AuditLogs.SingleAsync(a => a.ActionType == "RESOLUTION_CUSTOMER_ACCEPT" && a.NewDataJson!.Contains(command.RequestId.ToString()));
        Assert.Contains("SENT", audit.OldDataJson); Assert.Contains("CUSTOMER_ACCEPTED", audit.NewDataJson);
        await Assert.ThrowsAsync<ConflictException>(() => Resolution().RespondAsync(sale.Order, command with { Decision = "DECLINE", Reason = "Opposite" }, sale.Customer, default));
        await Assert.ThrowsAsync<ConflictException>(() => Resolution().RespondAsync(sale.Order, command with { RequestId = Guid.NewGuid(), Decision = "DECLINE", Reason = "Late response" }, sale.Customer, default));
        Assert.Equal(100, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
    }
    [ReceivingSqlFact]
    public async Task Proposal_permissions_IDOR_price_precision_and_inactive_customer_are_enforced_in_database()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var r = a.Resolution!;
        await Assert.ThrowsAsync<ForbiddenException>(() => Resolution().ActAsync(a.Summary.Id, new(Guid.NewGuid(), r.Version, "SEND", 1, "Message", null), F.ActorId, default));
        await Assert.ThrowsAsync<BadRequestException>(() => Resolution().ActAsync(a.Summary.Id, new(Guid.NewGuid(), r.Version, "SEND", 1.001m, "Message", null), manager, default));
        var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        var other = await SalesOrder((await Scenario()).Batch);
        await Assert.ThrowsAsync<NotFoundException>(() => Resolution().ForOrderAsync(sale.Order, other.Customer, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), other.Customer, default));
        await using var db = F.Context(); await db.Accounts.Where(x => x.AccountId == sale.Customer).ExecuteUpdateAsync(x => x.SetProperty(a => a.AccountStatus, "INACTIVE"));
        await Assert.ThrowsAsync<ForbiddenException>(() => Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default));
    }
    [ReceivingSqlFact]
    public async Task Customer_can_withdraw_acceptance_but_manager_rejection_never_unblocks_suspected_stock()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager(); var initial = await State(s.Batch);
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default);
        var accepted = (await Service().GetAsync(a.Summary.Id, default)).Resolution!;
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), accepted.Version, p.Id, p.Revision, "WITHDRAW", "Cần xem lại đề xuất."), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); Assert.False(view.CanApprove);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, "REJECT", "Kiểm tra lại số liệu."), manager, default);
        Assert.Equal(initial, await State(s.Batch));
        Assert.False((await new PickingRepository(Options(), Accessor).GetAsync(sale.Order, default)).Order.CanPick);
        Assert.Equal("WITHDRAWN", (await Service().GetAsync(a.Summary.Id, default)).Resolution!.Proposals.Single().Status);
    }
    [ReceivingSqlFact]
    public async Task External_stock_change_after_customer_response_conflicts_without_replacing_original_evidence()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "DECLINE", "Không mua nữa"), sale.Customer, default);
        await using var db = F.Context(); await db.Inventories.Where(i => i.ProductBatchId == s.Batch).ExecuteUpdateAsync(x => x.SetProperty(i => i.QuantityOnHand, 99).SetProperty(i => i.LastUpdatedAt, DateTime.UtcNow));
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); Assert.False(view.CanApprove);
        await Assert.ThrowsAsync<ConflictException>(() => Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default));
        Assert.Equal("PENDING", (await ReadEntity(a.Summary.Id)).AdjustmentStatus); Assert.Equal(99, await db.Inventories.Where(i => i.ProductBatchId == s.Batch).SumAsync(i => i.QuantityOnHand));
    }
    private sealed class FailResolutionAudit(string action) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, action))) throw new InvalidOperationException("Injected resolution write failure");
            return base.ReaderExecutingAsync(command, data, result, ct);
        }
    }
    [ReceivingSqlFact]
    public async Task Failure_persisting_customer_decline_rolls_back_order_release_proposal_notifications_and_hold_version()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single(); var initial = await State(s.Batch);
        await using var db = F.Context(); var notices = await db.Notifications.CountAsync();
        var failing = new StockAdjustmentRepository(Options(new FailResolutionAudit("RESOLUTION_CUSTOMER_DECLINE")), Accessor, Storage().Object);
        await Assert.ThrowsAsync<DbUpdateException>(() => failing.RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "DECLINE", "Không mua"), sale.Customer, default));
        Assert.Equal(initial, await State(s.Batch)); Assert.Equal(notices, await db.Notifications.CountAsync());
        var current = (await Service().GetAsync(a.Summary.Id, default)).Resolution!; Assert.Equal(sent.Version, current.Version); Assert.Equal("SENT", current.Proposals.Single().Status);
    }
    [ReceivingSqlFact]
    public async Task Failure_persisting_final_review_rolls_back_count_amendment_price_and_reallocation_together()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default); var initial = await State(s.Batch);
        await Assert.ThrowsAsync<DbUpdateException>(() => Reviews(manager, new FailResolutionAudit("REVIEW_STOCK_ADJUSTMENT")).ReviewAsync(a.Summary.Id, Decision(view), manager, default));
        Assert.Equal(initial, await State(s.Batch)); await using var db = F.Context();
        Assert.Equal(10000000, await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.TotalAmount).SingleAsync());
        Assert.False(await db.OrderAmendments.AnyAsync(m => m.OrderDetail.PurchaseOrderId == sale.Order)); Assert.Equal("PENDING", (await ReadEntity(a.Summary.Id)).AdjustmentStatus);
        Assert.False(await db.BatchSaleOffers.AnyAsync(o => o.ProductBatchId == s.Batch));
    }
}
