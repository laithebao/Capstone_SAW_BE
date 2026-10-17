using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Application.Features.Picking;
using SAW.Application.Features.GoodsIssues;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    private async Task<long> Order(long batchId)
    {
        await using var db=F.Context();var buyer=await db.Distributors.FirstOrDefaultAsync(d=>d.AccountId==F.ActorId);
        if(buyer==null){buyer=new Distributor {AccountId=F.ActorId,DistributorCode="SA-"+Guid.NewGuid().ToString("N")[..12],DistributorName="Adjustment buyer",ProfileStatus="ACTIVE",TaxCode="TEST"};db.Add(buyer);}
        var order=new PurchaseOrder {OrderCode="SA-"+Guid.NewGuid().ToString("N"),Distributor=buyer,OrderStatus="APPROVED",ApprovedByAccountId=F.ActorId,ApprovedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,DeliveryAddress="Fixture"};
        order.OrderDetails.Add(new OrderDetail {RequestedProductBatchId=batchId,CropTypeId=F.CropTypeId,RequestedQuantity=1,ApprovedQuantity=1,RequestedWeightKg=100,ApprovedWeightKg=100,Unit="Lô",UnitPrice=100});db.Add(order);await db.SaveChangesAsync();return order.PurchaseOrderId;
    }
    [ReceivingSqlFact]
    public async Task Pending_disposal_blocks_picking_issue_confirm_and_new_allocations_without_releasing_existing_holds()
    {
        var s=await Scenario();var order=await Order(s.Batch);var allocator=new OrderReservationAllocator(Options(),Accessor);Assert.True(await allocator.TryReserveAsync(order,F.ActorId,default));
        var belowHeld=await Request(s.Batch);await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(belowHeld,F.ActorId,default));
        var picking=new PickingService(new PickingRepository(Options(),Accessor));var detail=await picking.GetAsync(order,default);var line=detail.Lines.Single();var r=line.Reservations.Single();
        await picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,line.Id,r.InventoryId,s.Batch,40,0,"Fixture"),F.ActorId,default);
        var issues=new GoodsIssueService(new GoodsIssueRepository(Options(),Accessor));var draft=await issues.CreateAsync(new(Guid.NewGuid(),order,DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)),null,null,[new(r.Id,20)]),F.ActorId,default);
        var before=await State(s.Batch);await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);Assert.Equal(before,await State(s.Batch));
        var blocked=await picking.GetAsync(order,default);Assert.False(blocked.Lines.Single().Reservations.Single().CanPick);
        var pickError=await Assert.ThrowsAsync<ConflictException>(()=>picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,line.Id,r.InventoryId,s.Batch,10,40,null),F.ActorId,default));Assert.Contains("loại bỏ",pickError.Message);
        await Assert.ThrowsAsync<ConflictException>(()=>issues.ConfirmAsync(draft.Summary.Id,new(Guid.NewGuid(),draft.Snapshot),F.ActorId,default));
        Assert.All((await issues.OrderAsync(order,default)).Allocations,a=>Assert.False(a.CanIssue));
        var second=await Order(s.Batch);Assert.False(await allocator.TryReserveAsync(second,F.ActorId,default));Assert.Equal(before,await State(s.Batch));
        await using var db=F.Context();Assert.False(await db.InventoryReservations.AnyAsync(x=>x.OrderDetail.PurchaseOrderId==second));
    }
    [ReceivingSqlFact]
    public async Task Pending_disposal_excludes_catalog_and_direct_purchase_and_manager_availability_then_reject_does_not_unexpire_stock()
    {
        var s=await Scenario();await using var db=F.Context();var buyer=new Distributor {AccountId=F.ActorId,DistributorCode="SA-CATALOG",DistributorName="Catalog fixture",ProfileStatus="ACTIVE",TaxCode="TEST"};
        if(!await db.Distributors.AnyAsync(d=>d.AccountId==F.ActorId))db.Add(buyer);
        var qc=F.Inspection(s.Batch,"COMPLETED");qc.QcResult="PASS";qc.QualityGrade="E";qc.CompletedAt=DateTime.UtcNow.AddSeconds(1);db.Add(qc);
        db.Add(new BatchSaleOffer {ProductBatchId=s.Batch,IsPublished=true,WholeLotPrice=100,UpdatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
        var distributor=new DistributorOrderRepository(Options(),Accessor);Assert.Contains((await distributor.CatalogAsync(F.ActorId,new(PageSize:100),default)).Items,b=>b.BatchId==s.Batch);
        var a=await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);
        Assert.DoesNotContain((await distributor.CatalogAsync(F.ActorId,new(PageSize:100),default)).Items,b=>b.BatchId==s.Batch);
        var request=new CreateDistributorOrderRequest(Guid.NewGuid(),[new(s.Batch,100,100)],"Fixture address","0901234567",null,null);
        await Assert.ThrowsAsync<ConflictException>(()=>distributor.CreateAsync(F.ActorId,request,default));
        var manager=new WarehouseInventoryRepository(db,new OrderReservationAllocator(Options(),Accessor));var offers=await manager.GetSaleOffersAsync(null,1,100,default);
        Assert.False(offers.Items.Single(b=>b.BatchId==s.Batch).CanPublish);
        // Fixture review only, not a UC60 workflow test. Expiry remains an independent rule.
        await db.StockAdjustments.Where(x=>x.StockAdjustmentId==a.Summary.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.AdjustmentStatus,"REJECTED").SetProperty(a=>a.ReviewedByAccountId,F.ActorId));
        await db.ProductBatches.Where(x=>x.ProductBatchId==s.Batch).ExecuteUpdateAsync(x=>x.SetProperty(b=>b.ExpiryDate,DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(-1)));
        Assert.DoesNotContain((await distributor.CatalogAsync(F.ActorId,new(PageSize:100),default)).Items,b=>b.BatchId==s.Batch);
    }
    [ReceivingSqlFact]
    public async Task Issue_commits_first_then_old_disposal_snapshot_is_rejected()
    {
        var s=await Scenario();var order=await Order(s.Batch);Assert.True(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
        var picking=new PickingService(new PickingRepository(Options(),Accessor));var d=await picking.GetAsync(order,default);var l=d.Lines.Single();var r=l.Reservations.Single();
        await picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,l.Id,r.InventoryId,s.Batch,100,0,null),F.ActorId,default);
        var old=await Request(s.Batch,"DISPOSAL");var issues=new GoodsIssueService(new GoodsIssueRepository(Options(),Accessor));
        var draft=await issues.CreateAsync(new(Guid.NewGuid(),order,DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)),null,null,[new(r.Id,40)]),F.ActorId,default);
        await issues.ConfirmAsync(draft.Summary.Id,new(Guid.NewGuid(),draft.Snapshot),F.ActorId,default);
        await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(old,F.ActorId,default));
        var fresh=await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);Assert.Equal(60,fresh.Summary.SystemQuantity);
    }
}
