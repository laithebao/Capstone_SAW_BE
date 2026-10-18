using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using SAW.Application.Exceptions;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    private sealed class ObserveBatchLock:DbCommandInterceptor
    {
        public TaskCompletionSource<int> Session {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("INVENTORY WITH (TABLOCKX,HOLDLOCK)")) Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
            return base.NonQueryExecutingAsync(command,data,result,ct);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("FROM PRODUCT_BATCH WITH (UPDLOCK,HOLDLOCK)")) Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
            return base.ReaderExecutingAsync(command,data,result,ct);
        }
    }
    [ReceivingSqlFact]
    public async Task Inventory_change_committing_before_request_is_detected_after_real_SQL_lock_wait()
    {
        var s=await Scenario();var request=await Request(s.Batch,"DISPOSAL");
        await using var writer=F.Context(retry:false);await using var tx=await writer.Database.BeginTransactionAsync();
        var session=((SqlConnection)writer.Database.GetDbConnection()).ServerProcessId;
        await writer.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX,HOLDLOCK)"); await WarehouseBatchLock(writer,s.Batch);
        var observe=new ObserveBatchLock();var waiting=Service(observe).CreateAsync(request,F.ActorId,default);
        try
        {
            var waiter=await observe.Session.Task.WaitAsync(TimeSpan.FromSeconds(10));await F.AssertBlockedAsync(waiter,session);
            var inv=await writer.Inventories.SingleAsync(i=>i.ProductBatchId==s.Batch);inv.QuantityOnHand=99;inv.LastUpdatedAt=DateTime.UtcNow;
            await writer.SaveChangesAsync();await tx.CommitAsync();
        }
        finally {if(tx.GetDbTransaction().Connection!=null)await tx.RollbackAsync();}
        await Assert.ThrowsAsync<ConflictException>(()=>waiting);
        await using var check=F.Context();Assert.False(await check.StockAdjustments.AnyAsync(a=>a.Inventory.ProductBatchId==s.Batch));
    }
    private static Task WarehouseBatchLock(SAW.Infrastructure.Persistence.AppDbContext db,long batch)=>
        db.ProductBatches.FromSqlInterpolated($"SELECT * FROM PRODUCT_BATCH WITH (UPDLOCK,HOLDLOCK) WHERE ProductBatchID={batch}").LoadAsync();
    private sealed class PauseAudit:DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(command.Parameters.Cast<DbParameter>().Any(p=>Equals(p.Value,"CREATE_STOCK_ADJUSTMENT")))
            {Entered.TrySetResult(true);await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15),ct);}
            return await base.ReaderExecutingAsync(command,data,result,ct);
        }
    }
    [ReceivingSqlFact]
    public async Task Disposal_scope_range_lock_blocks_inventory_phantom_until_request_commits_then_old_approval_is_guarded()
    {
        var s=await Scenario();var request=await Request(s.Batch,"DISPOSAL");
        await using var writer=F.Context(retry:false);
        var location=new WarehouseLocation{LocationCode="SA-PHANTOM-"+Guid.NewGuid().ToString("N")[..8],ZoneName="Test",LocationStatus="ACTIVE"};
        writer.Add(location);await writer.SaveChangesAsync();
        var pause=new PauseAudit();var creating=Service(pause).CreateAsync(request,F.ActorId,default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var tx=await writer.Database.BeginTransactionAsync();var session=((SqlConnection)writer.Database.GetDbConnection()).ServerProcessId;
        var inserting=writer.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO INVENTORY(ProductBatchID,WarehouseLocationID,QuantityOnHand,ReservedQuantity,Unit,LastUpdatedAt) VALUES({s.Batch},{location.WarehouseLocationId},1,0,'kg',SYSUTCDATETIME())");
        try
        {
            // The inserted range must be blocked by the request's SERIALIZABLE inventory read.
            await Task.Delay(200);Assert.False(inserting.IsCompleted);
            await using var monitor=F.Context();
            var blocking=await monitor.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.dm_exec_requests WHERE session_id={session} AND blocking_session_id<>0").ToListAsync();
            Assert.Equal(1,blocking.Single());pause.Resume.TrySetResult(true);
            var a=await creating;await inserting;await tx.CommitAsync();Assert.Single(a.Lines);
            var fresh=await Service().ScopeAsync(s.Batch,"DISPOSAL",null,default);Assert.Equal(2,fresh.Stocks.Count);Assert.False(fresh.CanRequest);
        }
        finally {pause.Resume.TrySetResult(true);if(tx.GetDbTransaction().Connection!=null)await tx.RollbackAsync();}
    }
}
