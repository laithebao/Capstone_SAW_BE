using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Traceability.Services;
using SAW.Application.Features.DistributorOrders;
using SAW.Application.Features.Picking;
using SAW.Application.Features.GoodsIssues;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task New_inventory_after_disposal_request_is_not_silently_added_to_approval_scope()
    {
        var s=await Scenario([100,0]);var a=await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);var actor=await Manager();
        await using var db=F.Context();db.Add(new Inventory{ProductBatchId=s.Batch,QuantityOnHand=0,Unit="kg",LastUpdatedAt=DateTime.UtcNow,WarehouseLocation=new WarehouseLocation{LocationCode="NEW-"+Guid.NewGuid().ToString("N"),LocationStatus="ACTIVE",ZoneName="Test"}});await db.SaveChangesAsync();
        var view=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.Equal(3,view.CurrentStocks.Count);Assert.Equal(2,view.Request.Lines.Count);Assert.False(view.CanApprove);
        var before=await State(s.Batch);await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view),actor,default));Assert.Equal(before,await State(s.Batch));
    }
    [ReceivingSqlFact]
    public async Task Open_receipt_blocks_disposal_and_rejection_does_not_restore_expired_sale_eligibility()
    {
        var s=await Scenario(expired:true);var a=await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);var actor=await Manager();
        await using var db=F.Context();var location=await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.WarehouseLocationId).SingleAsync();
        db.Add(new GoodsReceipt{ProductBatchId=s.Batch,WarehouseLocationId=location,OperationAccountId=F.ActorId,ReceiptCode="OPEN-"+Guid.NewGuid().ToString("N"),ReceiptStatus="DRAFT",ReceivedAt=DateTime.UtcNow,ReceivedQuantity=100,WeightInKg=100,Unit="kg"});await db.SaveChangesAsync();
        var view=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.False(view.CanApprove);Assert.Contains("phiếu nhập",view.ApprovalLockReason);
        var before=await State(s.Batch);await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view),actor,default));
        await Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view,"REJECT","Review open receipt"),actor,default);Assert.Equal(before,await State(s.Batch));
        var order=await Order(s.Batch);Assert.False(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
    }
    [ReceivingSqlFact]
    public async Task Disposal_keeps_QC_QR_receipts_and_exposes_saved_UC29_history_but_public_policy_returns_gone()
    {
        var s=await Scenario();await using var db=F.Context();var qc=F.Inspection(s.Batch,"COMPLETED");qc.QcResult="PASS";qc.QualityGrade="E";qc.CompletedAt=DateTime.UtcNow;
        var token=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");var qr=new QrCode{ProductBatchId=s.Batch,PublicToken=token,TraceabilityUrl="https://test.invalid/trace/"+token,QrImageUrl="https://test.invalid/qr.png",IsActive=true,GeneratedAt=DateTime.UtcNow};db.AddRange(qc,qr);
        var location=await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.WarehouseLocationId).SingleAsync();
        var receipt=new GoodsReceipt{ProductBatchId=s.Batch,WarehouseLocationId=location,ReceiptCode="SAVED-"+Guid.NewGuid().ToString("N"),ReceiptStatus="COMMITTED",OperationAccountId=F.ActorId,ReceivedAt=DateTime.UtcNow,CommittedAt=DateTime.UtcNow,ReceivedQuantity=100,WeightInKg=100,Unit="kg"};db.Add(receipt);
        db.Add(new BatchSaleOffer{ProductBatchId=s.Batch,IsPublished=true,WholeLotPrice=100,UpdatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
        if(!await db.Distributors.AnyAsync(d=>d.AccountId==F.ActorId)){db.Add(new Distributor{AccountId=F.ActorId,DistributorCode="POLICY-"+Guid.NewGuid().ToString("N")[..12],DistributorName="Fixture buyer",ProfileStatus="ACTIVE",TaxCode="TEST"});await db.SaveChangesAsync();}
        var distributor=new DistributorOrderRepository(Options(),Accessor);Assert.Contains((await distributor.CatalogAsync(F.ActorId,new(PageSize:100),default)).Items,b=>b.BatchId==s.Batch);
        // Read policy is valid before disposal, including E/PASS.
        var publicService=new TraceabilityService(new TraceabilityRepository(db));Assert.NotNull(await publicService.GetAsync(token,default));
        var image=await Service().UploadAsync(new SAW.Infrastructure.QrCodes.PngQrCodeRenderer().RenderPng("fixture evidence"),F.ActorId,default);
        var a=await Service().CreateAsync((await Request(s.Batch,"DISPOSAL")) with{ImageIds=[image.Id]},F.ActorId,default);var actor=await Manager();var view=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.Equal(image,Assert.Single(view.Request.Images));
        await Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view,note:"Damaged stock"),actor,default);
        var before=await State(s.Batch);var auditCount=await db.AuditLogs.CountAsync();var related=await new ProductBatchQueryRepository(db).GetRelatedInformationAsync(s.Batch,default);
        var transaction=Assert.Single(related.WarehouseTransactions,t=>t.TransactionType=="STOCK_DISPOSAL");Assert.Equal("APPROVED",transaction.Status);Assert.Contains("Damaged stock",transaction.Note);
        Assert.Contains(related.StatusHistory,h=>h.NewStatus=="DISPOSED");
        Assert.Contains(related.GoodsReceipts,r=>r.Id==receipt.GoodsReceiptId&&r.ReceiptStatus=="COMMITTED");
        await Assert.ThrowsAsync<GoneException>(()=>publicService.GetAsync(token,default));
        var saved=await db.QrCodes.AsNoTracking().SingleAsync(q=>q.QrCodeId==qr.QrCodeId);Assert.True(saved.IsActive);Assert.Equal(token,saved.PublicToken);Assert.Equal(qr.TraceabilityUrl,saved.TraceabilityUrl);
        Assert.Equal("PASS",await db.QcInspections.Where(q=>q.QcInspectionId==qc.QcInspectionId).Select(q=>q.QcResult).SingleAsync());Assert.Equal(before,await State(s.Batch));Assert.Equal(auditCount,await db.AuditLogs.CountAsync());
        Assert.Equal(image,Assert.Single((await Service().GetAsync(a.Summary.Id,default)).Images));await Assert.ThrowsAsync<ConflictException>(()=>Service().RemoveUploadAsync(image.Id,F.ActorId,default));
        Assert.DoesNotContain((await distributor.CatalogAsync(F.ActorId,new(PageSize:100),default)).Items,b=>b.BatchId==s.Batch);
        await Assert.ThrowsAsync<ConflictException>(()=>distributor.CreateAsync(F.ActorId,new CreateDistributorOrderRequest(Guid.NewGuid(),[new(s.Batch,100,100)],"Fixture address","0901234567",null,null),default));
        var order=await Order(s.Batch);Assert.False(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
    }
    [ReceivingSqlFact]
    public async Task Disposed_batch_cannot_be_picked_or_issued_even_with_inconsistent_legacy_holds()
    {
        var s=await Scenario();var order=await Order(s.Batch);Assert.True(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
        var picking=new PickingService(new PickingRepository(Options(),Accessor));var detail=await picking.GetAsync(order,default);var line=detail.Lines.Single();var r=line.Reservations.Single();
        await picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,line.Id,r.InventoryId,s.Batch,40,0,null),F.ActorId,default);
        var issues=new GoodsIssueService(new GoodsIssueRepository(Options(),Accessor));var draft=await issues.CreateAsync(new(Guid.NewGuid(),order,DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)),null,null,[new(r.Id,20)]),F.ActorId,default);
        // Isolated deliberately inconsistent legacy fixture verifies independent terminal-state guards.
        await using var db=F.Context();await db.ProductBatches.Where(b=>b.ProductBatchId==s.Batch).ExecuteUpdateAsync(x=>x.SetProperty(b=>b.BatchStatus,"DISPOSED"));var before=await State(s.Batch);
        Assert.False((await picking.GetAsync(order,default)).Lines.Single().Reservations.Single().CanPick);
        await Assert.ThrowsAsync<ConflictException>(()=>picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,line.Id,r.InventoryId,s.Batch,1,40,null),F.ActorId,default));
        await Assert.ThrowsAsync<ConflictException>(()=>issues.ConfirmAsync(draft.Summary.Id,new(Guid.NewGuid(),draft.Snapshot),F.ActorId,default));Assert.Equal(before,await State(s.Batch));
    }
}
