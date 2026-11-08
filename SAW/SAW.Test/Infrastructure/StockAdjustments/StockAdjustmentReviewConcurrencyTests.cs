using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;
using SAW.Application.Features.GoodsIssues;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    private sealed class PauseReviewWriter(string action):DbCommandInterceptor
    {
        public TaskCompletionSource<int> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            // EF can batch several audit rows as MERGE ... INSERT, not INSERT INTO.
            if(command.CommandText.Contains("INSERT") && command.Parameters.Cast<DbParameter>().Any(p=>Equals(p.Value,action)))
            {Entered.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);await Resume.Task.WaitAsync(TimeSpan.FromSeconds(25),ct);}
            return await base.ReaderExecutingAsync(command,data,result,ct);
        }
    }
    private static async Task<int> WaitForPaused(Task writer,PauseReviewWriter pause)
    {
        var completed=await Task.WhenAny(writer,pause.Entered.Task).WaitAsync(TimeSpan.FromSeconds(20));
        if(completed==writer){await writer;Assert.Fail("Writer completed without reaching the audit persistence boundary.");}
        return await pause.Entered.Task;
    }
    private sealed class ObserveWarehouseLock:DbCommandInterceptor
    {
        public TaskCompletionSource<int> Session {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Observe(DbCommand command){if(command.CommandText.Contains("TABLOCKX"))Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default){Observe(command);return base.NonQueryExecutingAsync(command,data,result,ct);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default){Observe(command);return base.ReaderExecutingAsync(command,data,result,ct);}
    }
    private sealed class ObserveWriterSession:DbCommandInterceptor
    {
        public TaskCompletionSource<int> Session {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("TABLOCKX"))Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);return base.NonQueryExecutingAsync(command,data,result,ct);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("PRODUCT_BATCH WITH") || command.CommandText.Contains("PURCHASE_ORDER WITH"))Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);return base.ReaderExecutingAsync(command,data,result,ct);}
    }
    [ReceivingSqlFact]
    public async Task Picking_and_loss_report_creation_serialize_in_both_commit_orders_on_SQL_Server()
    {
        foreach(var reportFirst in new[]{true,false})
        {
            var s=await Scenario();var order=await Order(s.Batch);Assert.True(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
            var picking=new PickingService(new PickingRepository(Options(),Accessor));var d=await picking.GetAsync(order,default);var l=d.Lines.Single();var r=l.Reservations.Single();

            var request=await Request(s.Batch,actual:95);
            var pause=new PauseReviewWriter(reportFirst?"CREATE_STOCK_ADJUSTMENT":"CONFIRM_PICKING");
            Task Fulfill(DbCommandInterceptor? interceptor)=>new PickingService(new PickingRepository(Options(interceptor),Accessor)).ConfirmAsync(order,new(Guid.NewGuid(),r.Id,l.Id,r.InventoryId,s.Batch,100,0,null),F.ActorId,default);
            Task first=reportFirst?Service(pause).CreateAsync(request,F.ActorId,default):Fulfill(pause);
            var blocker=await WaitForPaused(first,pause);var observer=new ObserveWriterSession();
            Task second=reportFirst?Fulfill(observer):Service(observer).CreateAsync(request,F.ActorId,default);
            try{await F.AssertBlockedAsync(await observer.Session.Task.WaitAsync(TimeSpan.FromSeconds(10)),blocker);}
            finally{pause.Resume.TrySetResult(true);}
            await first;await Assert.ThrowsAsync<ConflictException>(()=>second);
            await using var db=F.Context();
            Assert.Equal(100,await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).SumAsync(i=>i.QuantityOnHand));
            Assert.Equal(reportFirst?1:0,await db.StockAdjustments.CountAsync(a=>a.Inventory.ProductBatchId==s.Batch));
            Assert.Equal(reportFirst?0:100,await db.InventoryReservations.Where(x=>x.InventoryId==r.InventoryId).Select(x=>x.PickedQuantity).SingleAsync());

        }
    }

    [ReceivingSqlFact]
    public async Task Post_picking_loss_report_is_rejected_before_or_during_issue_confirmation_without_resetting_history()
    {
        foreach(var reportFirst in new[]{true,false})
        {
            var scenario=await Scenario();var order=await Order(scenario.Batch);Assert.True(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
            var picking=new PickingService(new PickingRepository(Options(),Accessor));var d=await picking.GetAsync(order,default);var l=d.Lines.Single();var r=l.Reservations.Single();
            await picking.ConfirmAsync(order,new(Guid.NewGuid(),r.Id,l.Id,r.InventoryId,scenario.Batch,100,0,null),F.ActorId,default);
            var issues=new GoodsIssueService(new GoodsIssueRepository(Options(),Accessor));
            var draft=await issues.CreateAsync(new(Guid.NewGuid(),order,DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)),null,null,[new(r.Id,100)]),F.ActorId,default);
            var request=await Request(scenario.Batch,actual:95);
            if(reportFirst) {
                await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(request,F.ActorId,default));
                await issues.ConfirmAsync(draft.Summary.Id,new(Guid.NewGuid(),draft.Snapshot),F.ActorId,default);
            } else {
                var pause=new PauseReviewWriter("CONFIRM_GOODS_ISSUE");
                var first=new GoodsIssueService(new GoodsIssueRepository(Options(pause),Accessor)).ConfirmAsync(draft.Summary.Id,new(Guid.NewGuid(),draft.Snapshot),F.ActorId,default);
                var blocker=await WaitForPaused(first,pause);var observer=new ObserveWriterSession();var second=Service(observer).CreateAsync(request,F.ActorId,default);
                try{await F.AssertBlockedAsync(await observer.Session.Task.WaitAsync(TimeSpan.FromSeconds(10)),blocker);}finally{pause.Resume.TrySetResult(true);}
                await first;await Assert.ThrowsAsync<ConflictException>(()=>second);
            }
            await using var check=F.Context();Assert.False(await check.StockAdjustments.AnyAsync(a=>a.Inventory.ProductBatchId==scenario.Batch));
            Assert.Equal(100,await check.PickingHistories.Where(h=>h.InventoryReservation.OrderDetail.PurchaseOrderId==order).SumAsync(h=>h.PickedQuantity));
            Assert.Equal(0,await check.Inventories.Where(i=>i.ProductBatchId==scenario.Batch).SumAsync(i=>i.QuantityOnHand));
        }
    }
    [ReceivingSqlFact]
    public async Task Receipt_and_count_increase_share_global_capacity_lock_in_both_orders()
    {
        foreach(var reviewFirst in new[]{true,false})
        {
            var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch,actual:105),F.ActorId,default);var actor=await Manager();var view=await Reviews(actor).GetAsync(a.Summary.Id,default);
            var b=await F.BatchAsync();await using var setup=F.Context();await setup.ProductBatches.Where(x=>x.ProductBatchId==b.ProductBatchId).ExecuteUpdateAsync(x=>x.SetProperty(b=>b.BatchStatus,"APPROVED_FOR_STORAGE").SetProperty(b=>b.QualityGrade,"E"));
            var qc=F.Inspection(b.ProductBatchId,"COMPLETED");qc.QcResult="PASS";qc.QualityGrade="E";qc.CompletedAt=DateTime.UtcNow;setup.Add(qc);await setup.SaveChangesAsync();
            var location=await setup.Inventories.Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.WarehouseLocationId).SingleAsync();var receipts=new GoodsReceiptRepository(Options(),Accessor);var date=DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            var receiptId=await receipts.CreateAsync(new(b.ProductBatchId,location,date,null),F.ActorId,default);var snap=new GoodsReceiptSnapshot(location,date.ToDateTime(TimeOnly.MinValue),null);
            var total=await setup.Inventories.SumAsync(i=>i.QuantityOnHand);var setting=new SAW.Domain.Entities.WarehouseSetting{WarehouseSettingId=1,WarehouseName="Concurrency",MaxCapacityKg=total+100,UpdatedAt=DateTime.UtcNow};setup.Add(setting);await setup.SaveChangesAsync();
            try
            {
                // Pause approval while it owns the warehouse lock, or pause a real receipt transaction
                // immediately after it has taken that same lock (before its capacity read).
                var pause=new PauseReviewWriter("REVIEW_STOCK_ADJUSTMENT");
                if(reviewFirst)
                {
                    var first=Reviews(actor,pause).ReviewAsync(a.Summary.Id,Decision(view),actor,default);var blocker=await WaitForPaused(first,pause);var observe=new ObserveWarehouseLock();
                    var second=new GoodsReceiptRepository(Options(observe),Accessor).ConfirmAsync(receiptId,snap,F.ActorId,default);
                    try{await F.AssertBlockedAsync(await observe.Session.Task.WaitAsync(TimeSpan.FromSeconds(10)),blocker);}finally{pause.Resume.TrySetResult(true);}
                    await first;await Assert.ThrowsAsync<ConflictException>(()=>second);
                }
                else
                {
                    // Hold the exact SQL warehouse lock while executing the receipt's real confirmation;
                    // a command interceptor pauses it after the table lock has executed.
                    var receiptPause=new PauseReceiptCapacity();var first=new GoodsReceiptRepository(Options(receiptPause),Accessor).ConfirmAsync(receiptId,snap,F.ActorId,default);
                    var blocker=await receiptPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));var observe=new ObserveWarehouseLock();var second=Reviews(actor,observe).ReviewAsync(a.Summary.Id,Decision(view),actor,default);
                    try{await F.AssertBlockedAsync(await observe.Session.Task.WaitAsync(TimeSpan.FromSeconds(10)),blocker);}finally{receiptPause.Resume.TrySetResult(true);}
                    await first;await Assert.ThrowsAsync<ConflictException>(()=>second);
                }
                Assert.Equal(reviewFirst?105:100,await setup.Inventories.Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.QuantityOnHand).SingleAsync());Assert.Equal(reviewFirst?"DRAFT":"COMMITTED",await setup.GoodsReceipts.Where(x=>x.GoodsReceiptId==receiptId).Select(x=>x.ReceiptStatus).SingleAsync());
            }
            finally{setup.WarehouseSettings.Remove(setting);await setup.SaveChangesAsync();}
        }
    }
    private sealed class PauseReceiptCapacity:DbCommandInterceptor
    {
        public TaskCompletionSource<int> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("FROM [INVENTORY]")&&command.CommandText.Contains("SUM("))
            {Entered.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);await Resume.Task.WaitAsync(TimeSpan.FromSeconds(25),ct);}
            return await base.ReaderExecutingAsync(command,data,result,ct);
        }
    }
}
