using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
    private async Task<int> Manager()
    {
        await using var db=F.Context();var role=await db.Roles.SingleOrDefaultAsync(r=>r.RoleCode=="WAREHOUSE_MANAGER");
        if(role==null){role=new Role{RoleCode="WAREHOUSE_MANAGER",RoleName="Quản lý kho",IsActive=true};db.Add(role);}
        var key=Guid.NewGuid().ToString("N");var actor=new Account{Role=role,Username=key,Email=key+"@test.invalid",FullName="Manager "+key[..5],PasswordHash="unused-test",AccountStatus="ACTIVE"};
        db.Add(actor);await db.SaveChangesAsync();return actor.AccountId;
    }
    private StockAdjustmentReviewService Reviews(int actor,DbCommandInterceptor? interceptor=null)=>new(new StockAdjustmentRepository(Options(interceptor),
        new HttpContextAccessor{HttpContext=new DefaultHttpContext{User=new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id",actor.ToString()),new Claim(ClaimTypes.Role,"WAREHOUSE_MANAGER")],"test"))}},Storage().Object));
    private static ReviewStockAdjustmentRequest Decision(AdjustmentReviewDetail d,string action="APPROVE",string? note=null)=>new(Guid.NewGuid(),action,note,d.ReviewSnapshot);
    [ReceivingSqlFact]
    public async Task Count_100_to_95_assigns_once_preserves_batch_receiving_and_retry_does_not_duplicate_audit()
    {
        var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch),F.ActorId,default);var actor=await Manager();var reviews=Reviews(actor);
        var before=await reviews.GetAsync(a.Summary.Id,default);Assert.True(before.CanApprove);var input=Decision(before);
        var saved=await reviews.ReviewAsync(a.Summary.Id,input,actor,default);Assert.Equal("APPROVED",saved.Request.Summary.Status);Assert.False(saved.CanApprove);Assert.False(saved.CanReject);
        await using var db=F.Context();Assert.Equal(95,await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.QuantityOnHand).SingleAsync());
        var batch=await db.ProductBatches.SingleAsync(b=>b.ProductBatchId==s.Batch);Assert.Equal("IN_STOCK",batch.BatchStatus);Assert.Equal(100,batch.VerifiedWeightInKg);Assert.Equal("E",batch.QualityGrade);
        var state=await State(s.Batch);var audits=await db.AuditLogs.CountAsync();var history=await db.BatchStatusHistories.CountAsync(h=>h.ProductBatchId==s.Batch);
        await reviews.ReviewAsync(a.Summary.Id,input,actor,default);Assert.Equal(state,await State(s.Batch));Assert.Equal(audits,await db.AuditLogs.CountAsync());Assert.Equal(history,await db.BatchStatusHistories.CountAsync(h=>h.ProductBatchId==s.Batch));
        Assert.Single(await db.AuditLogs.Where(l=>l.ActionType=="REVIEW_STOCK_ADJUSTMENT"&&l.EntityId==a.Summary.Id.ToString()).ToListAsync());
        await Assert.ThrowsAsync<ConflictException>(()=>reviews.ReviewAsync(a.Summary.Id,input with{RequestId=Guid.NewGuid(),Decision="REJECT",ReviewNote="Later rejection"},actor,default));
    }
    [ReceivingSqlFact]
    public async Task Count_to_zero_is_not_disposal_and_positive_difference_only_uses_additional_capacity()
    {
        foreach(var actual in new[]{0m,105m})
        {
            var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch,actual:actual),F.ActorId,default);var actor=await Manager();
            await using var db=F.Context();var stock=await db.Inventories.Include(i=>i.WarehouseLocation).SingleAsync(i=>i.ProductBatchId==s.Batch);
            stock.WarehouseLocation.MaxWeightKg=105;await db.SaveChangesAsync();
            var d=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.True(d.CanApprove,d.ApprovalLockReason);await Reviews(actor).ReviewAsync(a.Summary.Id,Decision(d),actor,default);
            Assert.Equal(actual,await db.Inventories.AsNoTracking().Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.QuantityOnHand).SingleAsync());
            Assert.Equal("IN_STOCK",await db.ProductBatches.Where(b=>b.ProductBatchId==s.Batch).Select(b=>b.BatchStatus).SingleAsync());
        }
    }
    [ReceivingSqlFact]
    public async Task Count_increase_checks_location_and_total_warehouse_capacity_without_changing_pending_request()
    {
        var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch,actual:105),F.ActorId,default);var actor=await Manager();
        await using var db=F.Context();var stock=await db.Inventories.Include(i=>i.WarehouseLocation).SingleAsync(i=>i.ProductBatchId==s.Batch);
        stock.WarehouseLocation.MaxWeightKg=104;await db.SaveChangesAsync();
        var d=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.False(d.CanApprove);Assert.Contains("Vị trí kho",d.ApprovalLockReason);
        await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(d),actor,default));
        stock.WarehouseLocation.MaxWeightKg=105;var total=await db.Inventories.SumAsync(i=>i.QuantityOnHand);
        var setting=new WarehouseSetting{WarehouseSettingId=1,WarehouseName="Fixture capacity",MaxCapacityKg=total+4,UpdatedAt=DateTime.UtcNow};db.Add(setting);await db.SaveChangesAsync();
        try
        {
            d=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.False(d.CanApprove);Assert.Contains("Kho",d.ApprovalLockReason);
            await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(d),actor,default));
            setting.MaxCapacityKg=total+5;await db.SaveChangesAsync();d=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.True(d.CanApprove,d.ApprovalLockReason);
            await Reviews(actor).ReviewAsync(a.Summary.Id,Decision(d),actor,default);Assert.Equal(105,await db.Inventories.AsNoTracking().Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.QuantityOnHand).SingleAsync());
        }
        finally{db.WarehouseSettings.Remove(setting);await db.SaveChangesAsync();}
    }
    [ReceivingSqlFact]
    public async Task Stale_manager_view_and_stale_original_snapshot_are_distinct_conflicts_and_do_not_write()
    {
        var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch),F.ActorId,default);var actor=await Manager();var original=await Reviews(actor).GetAsync(a.Summary.Id,default);
        await using var db=F.Context();await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).ExecuteUpdateAsync(x=>x.SetProperty(i=>i.QuantityOnHand,99).SetProperty(i=>i.LastUpdatedAt,DateTime.UtcNow));
        var before=await State(s.Batch);var audits=await db.AuditLogs.CountAsync();
        await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(original),actor,default));
        var fresh=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.False(fresh.CanApprove);Assert.True(fresh.CanReject);Assert.Contains("lúc lập phiếu",fresh.ApprovalLockReason);
        await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.Summary.Id,Decision(fresh),actor,default));
        Assert.Equal(before,await State(s.Batch));Assert.Equal(audits,await db.AuditLogs.CountAsync());Assert.Equal("PENDING",(await ReadEntity(a.Summary.Id)).AdjustmentStatus);
    }
    [ReceivingSqlFact]
    public async Task Disposal_approves_all_positions_including_zero_preserves_evidence_and_records_one_status_history()
    {
        var s=await Scenario([40,60,0],true);var a=await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);var actor=await Manager();
        await using var db=F.Context();var original=await ReadEntity(a.Summary.Id);var lines=await db.StockAdjustmentLines.Where(l=>l.StockAdjustmentId==a.Summary.Id).OrderBy(l=>l.InventoryId).Select(l=>new{l.InventoryId,l.SystemQuantity,l.ActualQuantity,l.ReservedQuantity,l.InventoryUpdatedAt}).ToListAsync();
        var reviews=Reviews(actor);var view=await reviews.GetAsync(a.Summary.Id,default);Assert.True(view.CanApprove,view.ApprovalLockReason);var request=Decision(view,note:"Expired whole lot");
        var saved=await reviews.ReviewAsync(a.Summary.Id,request,actor,default);Assert.Equal("DISPOSED",saved.BatchStatus);Assert.Equal("APPROVED",saved.Request.Summary.Status);Assert.Equal(3,saved.CurrentStocks.Count);Assert.All(saved.CurrentStocks,i=>Assert.Equal(0,i.SystemQuantityKg));
        Assert.Equal(original.InventorySnapshotJson,(await ReadEntity(a.Summary.Id)).InventorySnapshotJson);Assert.Equal(original.SystemQuantity,saved.Request.Summary.SystemQuantity);
        Assert.Equal(JsonSerializer.Serialize(lines),JsonSerializer.Serialize(await db.StockAdjustmentLines.Where(l=>l.StockAdjustmentId==a.Summary.Id).OrderBy(l=>l.InventoryId).Select(l=>new{l.InventoryId,l.SystemQuantity,l.ActualQuantity,l.ReservedQuantity,l.InventoryUpdatedAt}).ToListAsync()));
        var history=Assert.Single(await db.BatchStatusHistories.Where(h=>h.ProductBatchId==s.Batch&&h.NewStatus=="DISPOSED").ToListAsync());Assert.Equal(actor,history.ChangedByAccountId);
        var audits=await db.AuditLogs.CountAsync();await reviews.ReviewAsync(a.Summary.Id,request,actor,default);Assert.Equal(audits,await db.AuditLogs.CountAsync());Assert.Equal(1,await db.BatchStatusHistories.CountAsync(h=>h.ProductBatchId==s.Batch&&h.NewStatus=="DISPOSED"));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(()=>db.StockAdjustments.Where(x=>x.StockAdjustmentId==a.Summary.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ReviewNote,"tamper")));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(()=>db.StockAdjustmentLines.Where(l=>l.StockAdjustmentId==a.Summary.Id).ExecuteDeleteAsync());
    }
    [ReceivingSqlFact]
    public async Task Linked_disposal_can_be_approved_but_rejection_preserves_stock_and_reservations()
    {
        var s=await Scenario();var order=await Order(s.Batch);Assert.True(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));
        var a=await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);var actor=await Manager();var view=await Reviews(actor).GetAsync(a.Summary.Id,default);Assert.True(view.CanApprove,view.ApprovalLockReason);Assert.NotEmpty(view.Reservations);
        var before=await State(s.Batch);
        var rejected=await Reviews(actor).ReviewAsync(a.Summary.Id,Decision(view,"REJECT"," Resolve held order first "),actor,default);Assert.Equal("Resolve held order first",rejected.Request.ReviewNote);Assert.Equal("REJECTED",rejected.Request.Summary.Status);Assert.Equal(before,await State(s.Batch));
        Assert.True((await Service().ScopeAsync(s.Batch,"DISPOSAL",null,default)).CanRequest);
    }
    [ReceivingSqlFact]
    public async Task Legacy_count_is_compatible_and_cannot_be_approved_below_current_reservations()
    {
        foreach(var held in new[]{false,true})
        {
            var s=await Scenario();
            if(held){var order=await Order(s.Batch);Assert.True(await new OrderReservationAllocator(Options(),Accessor).TryReserveAsync(order,F.ActorId,default));}
            await using var db=F.Context();var a=new StockAdjustment{AdjustmentCode="LEGACY-"+Guid.NewGuid().ToString("N"),InventoryId=s.Stocks[0],SystemQuantity=100,ActualQuantity=95,AdjustmentType="COUNTING_ERROR",Reason="Legacy count",CreatedByAccountId=F.ActorId,CreatedAt=DateTime.UtcNow};db.Add(a);await db.SaveChangesAsync();

            var actor=await Manager();var view=await Reviews(actor).GetAsync(a.StockAdjustmentId,default);Assert.False(view.HasOriginalSnapshot);
            if(held){Assert.False(view.CanApprove);await Assert.ThrowsAsync<ConflictException>(()=>Reviews(actor).ReviewAsync(a.StockAdjustmentId,Decision(view),actor,default));}
            else{Assert.True(view.CanApprove,view.ApprovalLockReason);await Reviews(actor).ReviewAsync(a.StockAdjustmentId,Decision(view),actor,default);Assert.Equal(95,await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.QuantityOnHand).SingleAsync());}
        }
    }
    private sealed class RejectReviewAudit:DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(command.Parameters.Cast<DbParameter>().Any(p=>Equals(p.Value,"REVIEW_STOCK_ADJUSTMENT")))throw new InvalidOperationException("Injected review audit failure");return base.ReaderExecutingAsync(command,data,result,ct);}
    }
    [ReceivingSqlFact]
    public async Task Failure_after_stock_and_disposal_status_save_rolls_back_inventory_header_history_and_audit()
    {
        foreach(var kind in new[]{"COUNT","DISPOSAL"})
        {
            var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch,kind),F.ActorId,default);var actor=await Manager();var view=await Reviews(actor).GetAsync(a.Summary.Id,default);
            var before=await State(s.Batch);await using var db=F.Context();var histories=await db.BatchStatusHistories.CountAsync();var audits=await db.AuditLogs.CountAsync();
            var e=await Assert.ThrowsAsync<DbUpdateException>(()=>Reviews(actor,new RejectReviewAudit()).ReviewAsync(a.Summary.Id,Decision(view),actor,default));Assert.Contains("Injected",e.InnerException!.Message);
            Assert.Equal(before,await State(s.Batch));Assert.Equal(histories,await db.BatchStatusHistories.CountAsync());Assert.Equal(audits,await db.AuditLogs.CountAsync());Assert.Equal("PENDING",(await ReadEntity(a.Summary.Id)).AdjustmentStatus);
        }
    }
    [ReceivingSqlFact]
    public async Task Two_managers_approve_vs_reject_have_one_decision_and_same_operation_replays_safely()
    {
        foreach(var opposing in new[]{false,true})
        {
            var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch),F.ActorId,default);var m1=await Manager();var m2=await Manager();var view=await Reviews(m1).GetAsync(a.Summary.Id,default);
            var first=Decision(view);var second=Decision(view,opposing?"REJECT":"APPROVE",opposing?"Reject concurrency":"Second approval");
            async Task<bool> Run(int actor,ReviewStockAdjustmentRequest r){try{await Reviews(actor).ReviewAsync(a.Summary.Id,r,actor,default);return true;}catch(ConflictException){return false;}}
            var result=await Task.WhenAll(Run(m1,first),Run(m2,second));Assert.Single(result,v=>v);
            var entity=await ReadEntity(a.Summary.Id);await using var db=F.Context();Assert.Equal(entity.AdjustmentStatus=="APPROVED"?95:100,await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).Select(i=>i.QuantityOnHand).SingleAsync());
            Assert.Equal(1,await db.AuditLogs.CountAsync(l=>l.ActionType=="REVIEW_STOCK_ADJUSTMENT"&&l.EntityId==a.Summary.Id.ToString()));
            var winner=result[0]?m1:m2;var input=result[0]?first:second;var auditCount=await db.AuditLogs.CountAsync();await Reviews(winner).ReviewAsync(a.Summary.Id,input,winner,default);Assert.Equal(auditCount,await db.AuditLogs.CountAsync());
        }
    }
    [ReceivingSqlFact]
    public async Task Manager_API_authorization_immutable_payload_validation_and_GET_have_no_writes()
    {
        var s=await Scenario();var a=await Service().CreateAsync(await Request(s.Batch),F.ActorId,default);var actor=await Manager();using var client=await F.ApiAsync();
        foreach(var role in new[]{"","OPERATION_STAFF","QC_STAFF","SUPPLIER","DISTRIBUTOR","ADMINISTRATOR"})
        {
            client.DefaultRequestHeaders.Authorization=role==""?null:new("Bearer",Token(F.ActorId,role));var expected=role==""?HttpStatusCode.Unauthorized:HttpStatusCode.Forbidden;
            Assert.Equal(expected,(await client.GetAsync("/api/warehouse-manager/stock-adjustments")).StatusCode);
            Assert.Equal(expected,(await client.PostAsJsonAsync($"/api/warehouse-manager/stock-adjustments/{a.Summary.Id}/review",Decision(await Reviews(actor).GetAsync(a.Summary.Id,default)))).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization=new("Bearer",Token(actor,"WAREHOUSE_MANAGER"));await using var db=F.Context();var audits=await db.AuditLogs.CountAsync();var state=await State(s.Batch);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/warehouse-manager/stock-adjustments?status=PENDING&sortBy=createdAsc&pageSize=1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync($"/api/warehouse-manager/stock-adjustments/{a.Summary.Id}")).StatusCode);Assert.Equal(state,await State(s.Batch));Assert.Equal(audits,await db.AuditLogs.CountAsync());
        var empty=await client.GetAsync("/api/warehouse-manager/stock-adjustments?search=does-not-exist-review-fixture");Assert.Equal(HttpStatusCode.OK,empty.StatusCode);
        var request=Decision(await Reviews(actor).GetAsync(a.Summary.Id,default));var node=JsonSerializer.SerializeToNode(request)!.AsObject();node["QuantityOnHand"]=0;
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/warehouse-manager/stock-adjustments/{a.Summary.Id}/review",node)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/warehouse-manager/stock-adjustments/{a.Summary.Id}/review",request with{Decision="REJECT",ReviewNote="   "})).StatusCode);
        client.DefaultRequestHeaders.Authorization=new("Bearer",Token(F.ActorId,"WAREHOUSE_MANAGER"));
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/warehouse-manager/stock-adjustments/{a.Summary.Id}/review",request)).StatusCode);
        client.DefaultRequestHeaders.Authorization=new("Bearer",Token(actor,"WAREHOUSE_MANAGER"));Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/warehouse-manager/stock-adjustments/{a.Summary.Id}/review",request)).StatusCode);
    }
}
