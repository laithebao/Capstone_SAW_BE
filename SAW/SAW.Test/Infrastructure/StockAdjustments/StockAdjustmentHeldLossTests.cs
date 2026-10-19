using SAW.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;
using SAW.Application.Features.GoodsIssues;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task Loss_below_reserved_can_be_reported_but_cannot_be_approved_or_picked_and_contract_is_unchanged()
    {
        var s=await Scenario();var order=await Order(s.Batch);
        var allocator=new OrderReservationAllocator(Options(),Accessor);Assert.True(await allocator.TryReserveAsync(order,F.ActorId,default));
        var picking=new PickingService(new PickingRepository(Options(),Accessor));var detail=await picking.GetAsync(order,default);
        var line=detail.Lines.Single();var r=line.Reservations.Single();var before=await State(s.Batch);
        var input=await Request(s.Batch,actual:95);var a=await Service().CreateAsync(input,F.ActorId,default);
        Assert.Equal(a.Summary.Id,(await Service().CreateAsync(input,F.ActorId,default)).Summary.Id);
        Assert.Equal(before,await State(s.Batch));Assert.Equal(95,a.Summary.ActualQuantity);Assert.Equal(100,a.Summary.SystemQuantity);
        var blocked=await picking.GetAsync(order,default);Assert.False(blocked.Order.CanPick);
        Assert.Contains("điều chỉnh tồn",blocked.Lines.Single().Reservations.Single().PickingLockReason);
        var error=await Assert.ThrowsAsync<ConflictException>(()=>picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,line.Id,r.InventoryId,s.Batch,100,0,null),F.ActorId,default));
        Assert.Contains("điều chỉnh tồn",error.Message);
        var actor=await Manager();var view=await Reviews(actor).GetAsync(a.Summary.Id,default);
        Assert.False(view.CanApprove);Assert.Contains("đơn",view.ApprovalLockReason);Assert.Contains(view.Reservations,x=>x.ReservedQuantityKg==100);
        await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view),actor,default));
        Assert.Equal(before,await State(s.Batch));
        await using var db=F.Context();Assert.Equal(100,await db.OrderDetails.Where(l=>l.OrderDetailId==line.Id).Select(l=>l.ApprovedWeightKg).SingleAsync());
        Assert.False(await db.AuditLogs.AnyAsync(l=>l.EntityName=="STOCK_ADJUSTMENT"&&l.EntityId==a.Summary.Id.ToString()&&l.ActionType=="REVIEW_STOCK_ADJUSTMENT"));
        await Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view,"REJECT","Rà soát đơn và xác minh hao hụt trước khi lập lại"),actor,default);
        Assert.Equal(before,await State(s.Batch));Assert.False((await picking.GetAsync(order,default)).Order.CanPick);
    }

    [ReceivingSqlFact]
    public async Task Pending_count_blocks_open_issue_and_new_allocation_without_changing_original_holds()
    {
        var s=await Scenario();var order=await Order(s.Batch);var allocator=new OrderReservationAllocator(Options(),Accessor);
        Assert.True(await allocator.TryReserveAsync(order,F.ActorId,default));
        var picking=new PickingService(new PickingRepository(Options(),Accessor));var d=await picking.GetAsync(order,default);var l=d.Lines.Single();var r=l.Reservations.Single();
        await picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,l.Id,r.InventoryId,s.Batch,100,0,null),F.ActorId,default);
        var issues=new GoodsIssueService(new GoodsIssueRepository(Options(),Accessor));
        var draft=await issues.CreateAsync(new(Guid.NewGuid(),order,DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)),null,null,[new(r.Id,100)]),F.ActorId,default);
        var before=await State(s.Batch);
        await Assert.ThrowsAsync<ConflictException>(async()=>await Service().CreateAsync(await Request(s.Batch,actual:95),F.ActorId,default));
        // Existing legacy pending evidence must still block a draft opened before the hold.
        await using(var legacy=F.Context()){legacy.StockAdjustments.Add(new StockAdjustment{InventoryId=s.Stocks[0],AdjustmentCode="LEGACY-HOLD-"+Guid.NewGuid().ToString("N"),SystemQuantity=100,ActualQuantity=95,AdjustmentType="COUNTING_ERROR",Reason="Existing pending evidence",CreatedByAccountId=F.ActorId,CreatedAt=DateTime.UtcNow});await legacy.SaveChangesAsync();}
        var error=await Assert.ThrowsAsync<ConflictException>(()=>issues.ConfirmAsync(draft.Summary.Id,new(Guid.NewGuid(),draft.Snapshot),F.ActorId,default));Assert.Contains("điều chỉnh tồn",error.Message);
        Assert.All((await issues.OrderAsync(order,default)).Allocations,a=>{Assert.False(a.CanIssue);Assert.Contains("điều chỉnh tồn",a.LockReason);});
        Assert.DoesNotContain((await issues.EligibleAsync(null,1,100,default)).Items,o=>o.Id==order);
        var other=await Order(s.Batch);Assert.False(await allocator.TryReserveAsync(other,F.ActorId,default));
        Assert.Equal(before,await State(s.Batch));
    }

    [ReceivingSqlFact]
    public async Task Resolving_holds_invalidates_old_loss_evidence_and_requires_a_new_reviewed_report()
    {
        var s=await Scenario();var order=await Order(s.Batch);
        Assert.True(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
        var a=await Service().CreateAsync(await Request(s.Batch,actual:95),F.ActorId,default);var actor=await Manager();
        var original=await ReadEntity(a.Summary.Id);
        // Simulate the independently completed order-resolution workflow ONLY in the disposable fixture.
        // Reporting/review production code never releases holds or changes the customer's contract.
        await using var db=F.Context();var held=await db.InventoryReservations.Where(r=>r.OrderDetail.PurchaseOrderId==order).ToListAsync();
        foreach(var r in held) {r.ReservationStatus="RELEASED";r.ReleasedAt=DateTime.UtcNow;}
        var purchase=await db.PurchaseOrders.FindAsync(order);purchase!.OrderStatus="CANCELLED";await db.SaveChangesAsync();
        var view=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.False(view.CanApprove);Assert.Contains("snapshot cũ",view.ApprovalLockReason);
        await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view),actor,default));
        await Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view,"REJECT","Lập lại sau khi đơn và giữ hàng đã được xử lý"),actor,default);
        Assert.Equal(original.InventorySnapshotJson,(await ReadEntity(a.Summary.Id)).InventorySnapshotJson);
        var fresh=await Service().CreateAsync((await Request(s.Batch,actual:95)) with {ReplacesStockAdjustmentId=a.Summary.Id},F.ActorId,default);
        var current=await Reviews(actor).GetAsync(fresh.Summary.Id,default);Assert.True(current.CanApprove,current.ApprovalLockReason);
        await Reviews(actor).ReviewAsync(fresh.Summary.Id,Decision(current),actor,default);
        Assert.Equal(95,await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).SumAsync(i=>i.QuantityOnHand));
        Assert.Equal(100,await db.OrderDetails.Where(l=>l.PurchaseOrderId==order).Select(l=>l.ApprovedWeightKg).SingleAsync());
    }
}
