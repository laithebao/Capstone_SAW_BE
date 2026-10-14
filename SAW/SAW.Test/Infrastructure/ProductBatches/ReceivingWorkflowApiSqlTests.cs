using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Features.GoodsReceipts.Services;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Services;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.GoodsReceipts;

namespace SAW.Test.Infrastructure.ProductBatches;

public sealed class ReceivingWorkflowApiSqlTests(GoodsReceiptSqlFixture fixture) : IClassFixture<GoodsReceiptSqlFixture>
{
    private ReceivingSqlFixture F=>fixture.Database;
    private AuthenticationHeaderValue Bearer(string role)=>new("Bearer",new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "uc28-test","uc28-test",[new Claim("account_id",F.ActorId.ToString()),new Claim(ClaimTypes.Role,role)],
        expires:DateTime.UtcNow.AddMinutes(20),signingCredentials:new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)),SecurityAlgorithms.HmacSha256))));
    private async Task<ProductBatch> SubmittedAsync()
    {
        var batch=await F.BatchAsync();await using var db=F.Context();
        await db.ProductBatches.Where(b=>b.ProductBatchId==batch.ProductBatchId).ExecuteUpdateAsync(s=>s
            .SetProperty(b=>b.BatchStatus,"SUBMITTED").SetProperty(b=>b.VerifiedQuantity,(decimal?)null)
            .SetProperty(b=>b.VerifiedWeightInKg,(decimal?)null));
        return batch;
    }
    [ReceivingSqlFact]
    public async Task Shared_read_roles_guest_outside_roles_and_receiving_write_matrix()
    {
        var batch=await F.BatchAsync();using var client=await F.ApiAsync();await using var db=F.Context();
        var before=await db.AuditLogs.CountAsync();var prior=await db.ProductBatches.AsNoTracking().SingleAsync(b=>b.ProductBatchId==batch.ProductBatchId);
        var path=$"/api/operation/product-batches/{batch.ProductBatchId}";
        foreach(var role in new[]{"OPERATION_STAFF","QC_STAFF","WAREHOUSE_MANAGER","ADMINISTRATOR"})
        {
            client.DefaultRequestHeaders.Authorization=Bearer(role);
            foreach(var route in new[]{"/api/operation/product-batches",path})
            {using var read=await client.GetAsync(route);Assert.Equal(HttpStatusCode.OK,read.StatusCode);}
            if(role=="OPERATION_STAFF")continue;
            using var confirm=await client.PutAsJsonAsync(path+$"/verify?supplierId={F.SupplierId}",new VerifyProductBatchRequest(10,100));
            using var reject=await client.PutAsJsonAsync(path+$"/reject?supplierId={F.SupplierId}",new RejectProductBatchRequest("Reason"));
            using var update=await client.PutAsJsonAsync(path+"/receiving-details",new UpdateProductBatchReceivingRequest(10,100,batch.UpdatedAt,batch.CreatedAt));
            Assert.Equal(HttpStatusCode.Forbidden,confirm.StatusCode);Assert.Equal(HttpStatusCode.Forbidden,reject.StatusCode);Assert.Equal(HttpStatusCode.Forbidden,update.StatusCode);
        }
        foreach(var role in new[]{"GUEST","SUPPLIER","DISTRIBUTOR"})
        {
            client.DefaultRequestHeaders.Authorization=role=="GUEST"?null:Bearer(role);
            foreach(var route in new[]{"/api/operation/product-batches",path})
            {using var read=await client.GetAsync(route);Assert.Equal(role=="GUEST"?HttpStatusCode.Unauthorized:HttpStatusCode.Forbidden,read.StatusCode);}
        }
        client.DefaultRequestHeaders.Authorization=Bearer("OPERATION_STAFF");
        foreach(var missingId in new[]{0L,999999L})
        {using var missing=await client.GetAsync($"/api/operation/product-batches/{missingId}");Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);}
        var after=await db.ProductBatches.AsNoTracking().SingleAsync(b=>b.ProductBatchId==batch.ProductBatchId);
        Assert.Equal(prior.UpdatedAt,after.UpdatedAt);Assert.Equal(prior.BatchStatus,after.BatchStatus);
        Assert.Equal(prior.VerifiedQuantity,after.VerifiedQuantity);Assert.Equal(before,await db.AuditLogs.CountAsync());
    }
    [ReceivingSqlFact]
    public async Task UC26_invalid_filter_http_matrix()
    {
        using var client=await F.ApiAsync();client.DefaultRequestHeaders.Authorization=Bearer("OPERATION_STAFF");
        foreach(var query in new[]{"page=0","pageSize=101","supplierId=0","cropTypeId=-1","batchCode="+new string('x',51),
            "batchCode=B%0A1","status=bad%20status","sortBy=unknown"})
        {using var response=await client.GetAsync("/api/operation/product-batches?"+query);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
    }
    [ReceivingSqlFact]
    public async Task UC27_http_receiving_and_reject_boundaries_and_supplier_status_checks()
    {
        var batch=await SubmittedAsync();using var client=await F.ApiAsync();client.DefaultRequestHeaders.Authorization=Bearer("OPERATION_STAFF");
        var verify=$"/api/operation/product-batches/{batch.ProductBatchId}/verify?supplierId={F.SupplierId}";
        foreach(var request in new[]{new VerifyProductBatchRequest(0,1),new(-1,1),new(1.0001m,1),new(1,0),
            new(1,1,VerifiedPackageCount:0),new(1,1,VerifiedPackageUnitWeightKg:0),new(1,1,new string('p',101)),new(1,1,ReceivingNote:new string('n',1001))})
        {using var response=await client.PutAsJsonAsync(verify,request);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
        using(var mismatch=await client.PutAsJsonAsync(verify.Replace($"supplierId={F.SupplierId}",$"supplierId={F.SupplierId+999}"),new VerifyProductBatchRequest(1,1)))
            Assert.Equal(HttpStatusCode.BadRequest,mismatch.StatusCode);
        using(var valid=await client.PutAsJsonAsync(verify,new VerifyProductBatchRequest(0.001m,0.001m,new string('p',100),1,0.001m,new string('n',1000))))
            Assert.Equal(HttpStatusCode.OK,valid.StatusCode);
        using(var repeated=await client.PutAsJsonAsync(verify,new VerifyProductBatchRequest(1,1)))Assert.Equal(HttpStatusCode.Conflict,repeated.StatusCode);
        var rejected=await SubmittedAsync();var reject=$"/api/operation/product-batches/{rejected.ProductBatchId}/reject?supplierId={F.SupplierId}";
        foreach(var reason in new[]{"   ",new string('r',1001)})
        {using var response=await client.PutAsJsonAsync(reject,new RejectProductBatchRequest(reason));Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
        using(var valid=await client.PutAsJsonAsync(reject,new RejectProductBatchRequest(" "+new string('r',1000)+" ")))Assert.Equal(HttpStatusCode.OK,valid.StatusCode);
        await using var check=F.Context();var saved=await check.ProductBatches.AsNoTracking().SingleAsync(b=>b.ProductBatchId==rejected.ProductBatchId);
        Assert.Equal(1000,saved.RejectionReason!.Length);Assert.Equal("REJECTED",saved.BatchStatus);Assert.Null(saved.VerifiedQuantity);
    }
    [ReceivingSqlFact]
    public async Task UC28_clear_all_optional_fields_stores_null_and_audit_without_state_change()
    {
        var batch=await F.BatchAsync();using var client=await F.ApiAsync();client.DefaultRequestHeaders.Authorization=Bearer("OPERATION_STAFF");
        using var response=await client.PutAsJsonAsync($"/api/operation/product-batches/{batch.ProductBatchId}/receiving-details",
            new UpdateProductBatchReceivingRequest(12,125,batch.UpdatedAt,batch.CreatedAt,"  ",null,null,"  "));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);await using var db=F.Context();
        var saved=await db.ProductBatches.AsNoTracking().SingleAsync(b=>b.ProductBatchId==batch.ProductBatchId);
        Assert.Null(saved.VerifiedPackagingType);Assert.Null(saved.VerifiedPackageCount);Assert.Null(saved.VerifiedPackageUnitWeightKg);Assert.Null(saved.ReceivingNote);
        Assert.Equal(12,saved.VerifiedQuantity);Assert.Equal(125,saved.VerifiedWeightInKg);Assert.Equal("PENDING_QC",saved.BatchStatus);
        Assert.Equal(batch.PackagingType,saved.PackagingType);Assert.Equal(batch.Note,saved.Note);
        Assert.Single(await db.AuditLogs.Where(a=>a.EntityName=="PRODUCT_BATCH"&&a.EntityId==$"ProductBatchId={batch.ProductBatchId}"&&a.Description=="Receiving details corrected.").ToListAsync());
    }
    [ReceivingSqlFact]
    public async Task UC27_confirm_and_reject_audit_failure_roll_back_status_history_and_receiving()
    {
        foreach(var confirm in new[]{true,false})
        {
            var batch=await SubmittedAsync();await using var before=F.Context();
            var historyCount=await before.BatchStatusHistories.CountAsync(h=>h.ProductBatchId==batch.ProductBatchId);var auditCount=await before.AuditLogs.CountAsync();
            await using var db=F.Context(new FailAudit());var repository=new ProductBatchVerificationRepository(db,new HttpContextAccessor());
            var history=new BatchStatusHistory{ProductBatchId=batch.ProductBatchId,OldStatus="SUBMITTED",NewStatus=confirm?"PENDING_QC":"REJECTED",ChangedAt=DateTime.UtcNow,ChangedByAccountId=F.ActorId};
            var failure=await Assert.ThrowsAsync<DbUpdateException>(()=>confirm
                ?repository.ConfirmAsync(batch.ProductBatchId,F.SupplierId,new(12,125,"New",12,10,"New"),history,default)
                :repository.RejectAsync(batch.ProductBatchId,F.SupplierId,"Rejected",history,default));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            await using var check=F.Context();var saved=await check.ProductBatches.AsNoTracking().SingleAsync(b=>b.ProductBatchId==batch.ProductBatchId);
            Assert.Equal("SUBMITTED",saved.BatchStatus);Assert.Null(saved.VerifiedQuantity);Assert.Null(saved.VerifiedWeightInKg);Assert.Null(saved.RejectionReason);
            Assert.Equal(batch.UpdatedAt,saved.UpdatedAt);Assert.Equal(historyCount,await check.BatchStatusHistories.CountAsync(h=>h.ProductBatchId==batch.ProductBatchId));
            Assert.Equal(auditCount,await check.AuditLogs.CountAsync());
        }
    }
    private sealed class FailAudit:DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {if(command.CommandText.Contains("INSERT INTO [AUDIT_LOG]"))throw new InvalidOperationException("Isolated audit fault");return ValueTask.FromResult(result);}
    }
    [ReceivingSqlFact]
    public async Task UC30_inclusive_dates_and_same_timestamp_id_pagination_http()
    {
        await using var db=F.Context();var prefix="R2-TIE-"+Guid.NewGuid().ToString("N")[..8];var stamp=new DateTime(2026,1,14);
        var location=new WarehouseLocation{LocationCode=prefix,ZoneName="Receiving test",LocationStatus="ACTIVE"};db.Add(location);await db.SaveChangesAsync();
        var ids=new List<long>();for(var i=0;i<3;i++)
        {var batch=await F.BatchAsync();var r=new GoodsReceipt{ReceiptCode=prefix+"-"+i,ProductBatchId=batch.ProductBatchId,WarehouseLocationId=location.WarehouseLocationId,
            OperationAccountId=F.ActorId,ReceivedQuantity=10,WeightInKg=100,Unit="Box",ReceivedAt=stamp,ReceiptStatus="DRAFT"};db.Add(r);await db.SaveChangesAsync();ids.Add(r.GoodsReceiptId);}
        using var client=await F.ApiAsync();client.DefaultRequestHeaders.Authorization=Bearer("OPERATION_STAFF");
        foreach(var sort in new[]{"receivedAtAsc","receivedAtDesc"})
        {var actual=new List<long>();for(var page=1;page<=3;page++)
            {using var response=await client.GetAsync($"/api/operation/goods-receipts?search={prefix}&fromDate=2026-01-14&toDate=2026-01-14&sortBy={sort}&page={page}&pageSize=1");
             Assert.Equal(HttpStatusCode.OK,response.StatusCode);var json=await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
             Assert.Equal(3,json.GetProperty("data").GetProperty("totalCount").GetInt32());actual.Add(Assert.Single(json.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());}
            Assert.Equal(sort=="receivedAtAsc"?ids:ids.AsEnumerable().Reverse(),actual);}
    }
    [ReceivingSqlFact]
    public async Task UC31_exact_capacity_today_note1000_and_below_boundary()
    {
        foreach(var limit in new[]{1000m,999.999m})
        {
            var batch=await F.BatchAsync();await using var db=F.Context();
            await db.ProductBatches.Where(b=>b.ProductBatchId==batch.ProductBatchId).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.BatchStatus,"APPROVED_FOR_STORAGE")
                .SetProperty(b=>b.QualityGrade,"E").SetProperty(b=>b.VerifiedQuantity,100m).SetProperty(b=>b.VerifiedWeightInKg,1000m));
            var qc=F.Inspection(batch.ProductBatchId,"COMPLETED");qc.QcResult="PASS";qc.QualityGrade="E";qc.CompletedAt=qc.StartedAt.AddSeconds(1);
            var location=new WarehouseLocation{LocationCode=Guid.NewGuid().ToString("N"),ZoneName="Receiving test",LocationStatus="ACTIVE",MaxWeightKg=limit};
            db.AddRange(qc,location);await db.SaveChangesAsync();
            var repository=new GoodsReceiptRepository(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString,o=>o.EnableRetryOnFailure(3)).Options,new HttpContextAccessor());
            var service=new GoodsReceiptService(new GoodsReceiptQueryRepository(db),repository);
            var request=new CreateGoodsReceiptRequest(batch.ProductBatchId,location.WarehouseLocationId,DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)),new string('n',1000));
            var draft=await service.CreateAsync(request,F.ActorId,default);Assert.Equal(1000,draft.Note!.Length);Assert.Equal("DRAFT",draft.ReceiptStatus);
            Assert.False(await db.Inventories.AnyAsync(i=>i.ProductBatchId==batch.ProductBatchId));
            if(limit<1000){await Assert.ThrowsAsync<ConflictException>(()=>service.ConfirmAsync(draft.Id,new(draft.Snapshot),F.ActorId,default));
                Assert.False(await db.Inventories.AnyAsync(i=>i.ProductBatchId==batch.ProductBatchId));continue;}
            var committed=await service.ConfirmAsync(draft.Id,new(draft.Snapshot),F.ActorId,default);Assert.Equal("COMMITTED",committed.ReceiptStatus);
            Assert.Equal(1000,(await db.Inventories.SingleAsync(i=>i.ProductBatchId==batch.ProductBatchId)).QuantityOnHand);
        }
    }
}
