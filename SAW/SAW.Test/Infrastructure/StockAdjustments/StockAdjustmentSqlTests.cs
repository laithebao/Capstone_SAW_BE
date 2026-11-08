using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Features.Picking;
using SAW.Application.Features.GoodsIssues;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.QrCodes;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests(StockAdjustmentSqlFixture fixture) : IClassFixture<StockAdjustmentSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Database;
    private IHttpContextAccessor Accessor => new HttpContextAccessor { HttpContext=new DefaultHttpContext {
        User=new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id",F.ActorId.ToString()),new Claim(ClaimTypes.Role,"OPERATION_STAFF")],"test")) } };
    private DbContextOptions<AppDbContext> Options(DbCommandInterceptor? interceptor=null)
    { var b=new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString,s=>s.EnableRetryOnFailure(4,TimeSpan.FromMilliseconds(100),null)); if(interceptor!=null)b.AddInterceptors(interceptor);return b.Options; }
    private static Mock<IQrImageStorage> Storage()
    { var m=new Mock<IQrImageStorage>();m.Setup(s=>s.UploadAsync(It.IsAny<string>(),It.IsAny<byte[]>(),It.IsAny<CancellationToken>())).ReturnsAsync((string id,byte[] _,CancellationToken _)=>"https://test.invalid/"+id+".png");m.Setup(s=>s.DeleteAsync(It.IsAny<string>(),It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);return m; }
    private StockAdjustmentService Service(DbCommandInterceptor? interceptor=null,IQrImageStorage? storage=null)=>new(new StockAdjustmentRepository(Options(interceptor),Accessor,storage??Storage().Object));
    private async Task<(long Batch,List<long> Stocks)> Scenario(decimal[]? quantities=null,bool expired=false)
    {
        var b=await F.BatchAsync(); await using var db=F.Context();var batch=await db.ProductBatches.FindAsync(b.ProductBatchId);
        batch!.BatchStatus="IN_STOCK";batch.QualityGrade="E";batch.ExpiryDate=DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(expired?-1:30);
        var stocks=(quantities??[100]).Select(q=>new Inventory {ProductBatchId=b.ProductBatchId,QuantityOnHand=q,Unit="kg",LastUpdatedAt=DateTime.UtcNow,
            WarehouseLocation=new WarehouseLocation {LocationCode="SA-"+Guid.NewGuid().ToString("N")[..12],ZoneName="Test",LocationStatus="ACTIVE"}}).ToList();
        db.AddRange(stocks);await db.SaveChangesAsync();return(b.ProductBatchId,stocks.Select(i=>i.InventoryId).ToList());
    }
    private async Task<CreateStockAdjustmentRequest> Request(long batch,string kind="COUNT",long? inventory=null,decimal? actual=95)
    {
        if(kind=="COUNT" && inventory==null){await using var db=F.Context();inventory=await db.Inventories.Where(i=>i.ProductBatchId==batch).Select(i=>(long?)i.InventoryId).FirstAsync();}
        var scope=await Service().ScopeAsync(batch,kind,inventory,default);
        return new(Guid.NewGuid(),batch,kind,kind=="COUNT"?inventory:null,kind=="COUNT"?actual:null,kind=="COUNT"?"COUNTING_ERROR":"SPOILAGE"," Test reason ",scope.Snapshot,[]);
    }
    private async Task<string> State(long batch)
    {
        await using var db=F.Context();var ids=db.Inventories.Where(i=>i.ProductBatchId==batch).Select(i=>i.InventoryId);
        return JsonSerializer.Serialize(new {Batch=await db.ProductBatches.Where(b=>b.ProductBatchId==batch).Select(b=>new {b.BatchStatus,b.QualityGrade,b.VerifiedQuantity,b.VerifiedWeightInKg,b.UpdatedAt}).SingleAsync(),
            Stock=await db.Inventories.Where(i=>i.ProductBatchId==batch).OrderBy(i=>i.InventoryId).Select(i=>new{i.InventoryId,i.QuantityOnHand,i.ReservedQuantity,i.LastUpdatedAt}).ToListAsync(),
            Reservations=await db.InventoryReservations.Where(r=>ids.Contains(r.InventoryId)).OrderBy(r=>r.InventoryReservationId).Select(r=>new{r.InventoryReservationId,r.ReservedQuantity,r.PickedQuantity,r.ReservationStatus}).ToListAsync(),
            Picks=await db.PickingHistories.CountAsync(p=>ids.Contains(p.InventoryReservation.InventoryId)),Issues=await db.GoodsIssueDetails.CountAsync(d=>ids.Contains(d.InventoryId))});
    }
    [ReceivingSqlFact]
    public async Task Count_saves_server_snapshots_and_computed_difference_without_stock_changes_and_replays_once()
    {
        var s=await Scenario();var r=await Request(s.Batch);var before=await State(s.Batch);
        var a=await Service().CreateAsync(r,F.ActorId,default);Assert.Equal(before,await State(s.Batch));
        Assert.Equal("PENDING",a.Summary.Status);Assert.Equal(100,a.Summary.SystemQuantity);Assert.Equal(95,a.Summary.ActualQuantity);Assert.Equal(-5,a.Summary.DifferenceQuantity);
        Assert.Single(a.Lines);Assert.Equal("Test reason",a.Reason);Assert.Equal(F.ActorId,(await ReadEntity(a.Summary.Id)).CreatedByAccountId);
        Assert.Equal(a.Summary.Id,(await Service().CreateAsync(r,F.ActorId,default)).Summary.Id);
        await using var db=F.Context();Assert.Single(await db.AuditLogs.Where(x=>x.EntityName=="STOCK_ADJUSTMENT"&&x.ActionType=="CREATE_STOCK_ADJUSTMENT"&&x.EntityId==a.Summary.Id.ToString()).ToListAsync());
        await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(r with{Reason="Different"},F.ActorId,default));
    }
    private async Task<StockAdjustment> ReadEntity(long id){await using var db=F.Context();return await db.StockAdjustments.AsNoTracking().SingleAsync(a=>a.StockAdjustmentId==id);}
    [ReceivingSqlFact]
    public async Task Whole_lot_disposal_contains_every_location_including_zero_rows_expired_stock_and_preserves_all_quantities()
    {
        var s=await Scenario([40,60,0],true);var before=await State(s.Batch);var input=await Request(s.Batch,"DISPOSAL");
        var a=await Service().CreateAsync(input,F.ActorId,default);Assert.Equal(before,await State(s.Batch));Assert.Equal(3,a.Lines.Count);Assert.Equal(100,a.Summary.SystemQuantity);Assert.Equal(0,a.Summary.ActualQuantity);
        Assert.All(a.Lines,l=>Assert.Equal(0,l.ActualQuantity));var saved=await ReadEntity(a.Summary.Id);Assert.NotNull(saved.InventorySnapshotJson);
        var lookup=await Service().BatchesAsync(a.Summary.BatchCode,1,20,default);Assert.Contains(lookup.Items,b=>b.Id==s.Batch);
        Assert.False((await Service().ScopeAsync(s.Batch,"DISPOSAL",null,default)).CanRequest);
        await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(input with{RequestId=Guid.NewGuid()},F.ActorId,default));
    }
    [ReceivingSqlFact]
    public async Task Empty_stock_cancelled_batch_invalid_relationship_zero_difference_and_inactive_count_are_blocked()
    {
        var s=await Scenario([0]);var disposal=await Request(s.Batch,"DISPOSAL");await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(disposal,F.ActorId,default));
        var other=await Scenario();await Assert.ThrowsAsync<BadRequestException>(()=>Service().ScopeAsync(s.Batch,"COUNT",other.Stocks[0],default));
        var equal=await Request(other.Batch,actual:100);await Assert.ThrowsAsync<BadRequestException>(()=>Service().CreateAsync(equal,F.ActorId,default));
        await using var db=F.Context();await db.ProductBatches.Where(b=>b.ProductBatchId==other.Batch).ExecuteUpdateAsync(x=>x.SetProperty(b=>b.BatchStatus,"CANCELLED"));
        Assert.False((await Service().ScopeAsync(other.Batch,"COUNT",other.Stocks[0],default)).CanRequest);
        var inactive=await Scenario();await db.WarehouseLocations.Where(l=>l.Inventories.Any(i=>i.ProductBatchId==inactive.Batch)).ExecuteUpdateAsync(x=>x.SetProperty(l=>l.LocationStatus,"INACTIVE"));
        Assert.False((await Service().ScopeAsync(inactive.Batch,"COUNT",inactive.Stocks[0],default)).CanRequest);
    }
    [ReceivingSqlFact]
    public async Task Stale_quantity_reservation_or_inventory_set_conflicts_without_creating_request()
    {
        var s=await Scenario();var input=await Request(s.Batch);
        await using var db=F.Context();await db.Inventories.Where(i=>i.ProductBatchId==s.Batch).ExecuteUpdateAsync(x=>x.SetProperty(i=>i.QuantityOnHand,99));
        await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(input,F.ActorId,default));
        var disposal=await Request(s.Batch,"DISPOSAL");
        db.Inventories.Add(new Inventory {ProductBatchId=s.Batch,QuantityOnHand=1,Unit="kg",LastUpdatedAt=DateTime.UtcNow,WarehouseLocation=new WarehouseLocation {LocationCode="SA-"+Guid.NewGuid().ToString("N")[..12],ZoneName="Test",LocationStatus="ACTIVE"}});await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(disposal,F.ActorId,default));Assert.False(await db.StockAdjustments.AnyAsync(a=>a.Inventory.ProductBatchId==s.Batch));
    }
    [ReceivingSqlFact]
    public async Task Concurrent_duplicate_disposals_and_count_disposal_conflicts_have_one_pending_request()
    {
        foreach(var mixed in new[]{false,true})
        {
            var s=await Scenario();var first=await Request(s.Batch,mixed?"COUNT":"DISPOSAL");var second=await Request(s.Batch,"DISPOSAL");
            async Task<bool> Run(CreateStockAdjustmentRequest r){try{await Service().CreateAsync(r,F.ActorId,default);return true;}catch(ConflictException){return false;}}
            var results=await Task.WhenAll(Run(first),Run(second));Assert.Single(results,x=>x);
            await using var db=F.Context();Assert.Equal(1,await db.StockAdjustments.CountAsync(a=>a.Inventory.ProductBatchId==s.Batch));
        }
    }
    [ReceivingSqlFact]
    public async Task Same_operation_concurrently_is_idempotent_and_different_inventory_counts_can_coexist()
    {
        var s=await Scenario([100,100]);var r=await Request(s.Batch,inventory:s.Stocks[0]);
        async Task<AdjustmentDetail?> Run(){try{return await Service().CreateAsync(r,F.ActorId,default);}catch(ConflictException e){Assert.StartsWith("Yêu cầu đang được xử lý",e.Message);return null;}}
        var results=await Task.WhenAll(Run(),Run());Assert.Contains(results,a=>a is not null);
        var replay=await Service().CreateAsync(r,F.ActorId,default);Assert.All(results.Where(a=>a!=null),a=>Assert.Equal(replay.Summary.Id,a!.Summary.Id));
        var second=await Service().CreateAsync(await Request(s.Batch,inventory:s.Stocks[1]),F.ActorId,default);Assert.NotEqual(replay.Summary.Id,second.Summary.Id);
    }
    private sealed class RejectAudit:DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        { if(command.Parameters.Cast<DbParameter>().Any(p=>Equals(p.Value,"CREATE_STOCK_ADJUSTMENT")))throw new InvalidOperationException("Injected adjustment audit failure");return base.ReaderExecutingAsync(command,data,result,ct); }
    }
    [ReceivingSqlFact]
    public async Task Audit_failure_rolls_back_header_lines_and_image_link()
    {
        var s=await Scenario();var before=await State(s.Batch);var r=await Request(s.Batch,"DISPOSAL");
        var image=await Service().UploadAsync(new PngQrCodeRenderer().RenderPng("https://test.invalid"),F.ActorId,default);
        var e=await Assert.ThrowsAsync<DbUpdateException>(()=>Service(new RejectAudit()).CreateAsync(r with{ImageIds=[image.Id]},F.ActorId,default));Assert.Contains("Injected",e.InnerException!.Message);
        await using var db=F.Context();Assert.False(await db.StockAdjustments.AnyAsync(a=>a.Inventory.ProductBatchId==s.Batch));Assert.Null((await db.StockAdjustmentImages.FindAsync(image.Id))!.StockAdjustmentId);Assert.Equal(before,await State(s.Batch));
    }
    [ReceivingSqlFact]
    public async Task Images_are_fully_decoded_owned_linked_atomically_and_cannot_be_deleted_when_referenced()
    {
        var s=await Scenario();var storage=Storage();var service=Service(storage:storage.Object);
        var image=await service.UploadAsync(new PngQrCodeRenderer().RenderPng("https://test.invalid"),F.ActorId,default);
        var input=(await Request(s.Batch)) with{ImageIds=[image.Id]};
        await using(var db=F.Context()){await db.StockAdjustmentImages.Where(i=>i.StockAdjustmentImageId==image.Id).ExecuteUpdateAsync(x=>x.SetProperty(i=>i.UploadedByAccountId,F.ActorId));}
        var a=await service.CreateAsync(input,F.ActorId,default);Assert.Equal(image,Assert.Single(a.Images));
        await Assert.ThrowsAsync<ConflictException>(()=>service.RemoveUploadAsync(image.Id,F.ActorId,default));storage.Verify(x=>x.DeleteAsync(It.IsAny<string>(),It.IsAny<CancellationToken>()),Times.Never);
        var other=await Scenario();var reused=(await Request(other.Batch)) with{ImageIds=[image.Id]};await Assert.ThrowsAsync<ConflictException>(()=>service.CreateAsync(reused,F.ActorId,default));
    }
    [ReceivingSqlFact]
    public async Task Upload_failure_creates_no_image_ticket_and_unlinked_upload_can_be_removed()
    {
        var storage=Storage();storage.Setup(s=>s.UploadAsync(It.IsAny<string>(),It.IsAny<byte[]>(),It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("storage unavailable"));
        await using var db=F.Context();var before=await db.StockAdjustmentImages.CountAsync();
        await Assert.ThrowsAsync<ConflictException>(()=>Service(storage:storage.Object).UploadAsync(new PngQrCodeRenderer().RenderPng("test"),F.ActorId,default));Assert.Equal(before,await db.StockAdjustmentImages.CountAsync());
        var valid=Service();var image=await valid.UploadAsync(new PngQrCodeRenderer().RenderPng("test"),F.ActorId,default);await valid.RemoveUploadAsync(image.Id,F.ActorId,default);Assert.Null(await db.StockAdjustmentImages.FindAsync(image.Id));
    }
    [ReceivingSqlFact]
    public async Task Other_staff_upload_cannot_be_linked_or_removed_by_current_actor()
    {
        var s=await Scenario();await using var db=F.Context();var key=Guid.NewGuid().ToString("N");
        var other=new Account{RoleId=await db.Accounts.Where(a=>a.AccountId==F.ActorId).Select(a=>a.RoleId).SingleAsync(),Username=key,Email=key+"@test.invalid",FullName="Other staff",PasswordHash="unused",AccountStatus="ACTIVE"};db.Add(other);await db.SaveChangesAsync();
        var image=await Service().UploadAsync(new PngQrCodeRenderer().RenderPng("test"),other.AccountId,default);
        var input=(await Request(s.Batch)) with{ImageIds=[image.Id]};
        await Assert.ThrowsAsync<ConflictException>(()=>Service().CreateAsync(input,F.ActorId,default));
        await Assert.ThrowsAsync<ForbiddenException>(()=>Service().RemoveUploadAsync(image.Id,F.ActorId,default));
        await Service().RemoveUploadAsync(image.Id,other.AccountId,default);
    }
    [ReceivingSqlFact]
    public async Task Later_upload_cleans_abandoned_unlinked_images_and_preserves_referenced_evidence()
    {
        var s=await Scenario();var storage=Storage();var service=Service(storage:storage.Object);var png=new PngQrCodeRenderer().RenderPng("test");
        var abandoned=await service.UploadAsync(png,F.ActorId,default);var linked=await service.UploadAsync(png,F.ActorId,default);
        await service.CreateAsync((await Request(s.Batch)) with{ImageIds=[linked.Id]},F.ActorId,default);
        await using var db=F.Context();await db.StockAdjustmentImages.Where(i=>i.StockAdjustmentImageId==abandoned.Id||i.StockAdjustmentImageId==linked.Id).ExecuteUpdateAsync(x=>x.SetProperty(i=>i.UploadedAt,DateTime.UtcNow.AddDays(-2)));
        await service.UploadAsync(png,F.ActorId,default);Assert.Null(await db.StockAdjustmentImages.FindAsync(abandoned.Id));Assert.NotNull(await db.StockAdjustmentImages.FindAsync(linked.Id));
        storage.Verify(x=>x.DeleteAsync(It.Is<string>(id=>id.EndsWith(abandoned.Id.ToString("N"))),It.IsAny<CancellationToken>()),Times.Once);
        storage.Verify(x=>x.DeleteAsync(It.Is<string>(id=>id.EndsWith(linked.Id.ToString("N"))),It.IsAny<CancellationToken>()),Times.Never);
    }
    [ReceivingSqlFact]
    public async Task Search_filters_sort_paging_detail_reviews_and_GET_do_not_write()
    {
        var s=await Scenario([100,100]);var a=await Service().CreateAsync(await Request(s.Batch,inventory:s.Stocks[0]),F.ActorId,default);
        var b=await Service().CreateAsync(await Request(s.Batch,inventory:s.Stocks[1]),F.ActorId,default);
        await using var db=F.Context();await db.StockAdjustments.Where(x=>x.StockAdjustmentId==a.Summary.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.AdjustmentStatus,"REJECTED").SetProperty(v=>v.ReviewedByAccountId,F.ActorId).SetProperty(v=>v.ReviewedAt,DateTime.UtcNow).SetProperty(v=>v.ReviewNote,"Fixture review only"));
        var audits=await db.AuditLogs.CountAsync();var state=await State(s.Batch);
        var rows=await Service().SearchAsync(new(ProductBatchId:s.Batch,SortBy:"codeAsc",PageSize:1),default);Assert.Equal(2,rows.TotalCount);Assert.Single(rows.Items);
        var reviewed=await Service().GetAsync(a.Summary.Id,default);Assert.Equal("REJECTED",reviewed.Summary.Status);Assert.Equal("Fixture review only",reviewed.ReviewNote);Assert.NotNull(reviewed.ReviewedBy);
        var pending=await Service().SearchAsync(new(Search:b.Summary.AdjustmentCode,RequestKind:"COUNT",Status:"PENDING",AdjustmentType:"COUNTING_ERROR"),default);Assert.Equal(b.Summary.Id,Assert.Single(pending.Items).Id);
        Assert.Equal(state,await State(s.Batch));Assert.Equal(audits,await db.AuditLogs.CountAsync());
    }
    [ReceivingSqlFact]
    public async Task Whole_lot_request_cannot_be_approved_by_legacy_single_inventory_update()
    {
        var s=await Scenario([40,60]);var a=await Service().CreateAsync(await Request(s.Batch,"DISPOSAL"),F.ActorId,default);var before=await State(s.Batch);
        await using var db=F.Context(retry:false);await using(var tx=await db.Database.BeginTransactionAsync())
        {
            await db.Inventories.Where(i=>i.InventoryId==s.Stocks[0]).ExecuteUpdateAsync(x=>x.SetProperty(i=>i.QuantityOnHand,0));
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(()=>db.StockAdjustments.Where(x=>x.StockAdjustmentId==a.Summary.Id).ExecuteUpdateAsync(x=>x.SetProperty(i=>i.AdjustmentStatus,"APPROVED").SetProperty(i=>i.ReviewedByAccountId,F.ActorId)));
            await tx.RollbackAsync();
        }
        Assert.Equal(before,await State(s.Batch));Assert.Equal("PENDING",(await ReadEntity(a.Summary.Id)).AdjustmentStatus);
    }
    private static string Token(int id,string role)=>new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test","uc28-test",[new Claim("account_id",id.ToString()),new Claim(ClaimTypes.Role,role)],expires:DateTime.UtcNow.AddHours(1),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)),SecurityAlgorithms.HmacSha256)));
    [ReceivingSqlFact]
    public async Task HTTP_role_matrix_forged_fields_and_invalid_image_are_rejected()
    {
        var s=await Scenario();var request=await Request(s.Batch);using var client=await F.ApiAsync();
        foreach(var role in new[]{"","DISTRIBUTOR","SUPPLIER","QC_STAFF","WAREHOUSE_MANAGER","ADMINISTRATOR"})
        {
            client.DefaultRequestHeaders.Authorization=role==""?null:new("Bearer",Token(F.ActorId,role));
            Assert.Equal(role==""?HttpStatusCode.Unauthorized:HttpStatusCode.Forbidden,(await client.GetAsync("/api/operation/stock-adjustments")).StatusCode);
            Assert.Equal(role==""?HttpStatusCode.Unauthorized:HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/operation/stock-adjustments",request)).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization=new("Bearer",Token(F.ActorId,"OPERATION_STAFF"));
        foreach(var name in new[]{"ReviewedByAccountId","CreatedByAccountId","AdjustmentStatus","SystemQuantity","ImageUrl"})
        {
            var node=JsonSerializer.SerializeToNode(request)!.AsObject();node[name]="forged";
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/operation/stock-adjustments",node)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/operation/stock-adjustments?pageSize=101")).StatusCode);
        using var form=new MultipartFormDataContent();var fake=new ByteArrayContent("fake PNG"u8.ToArray());fake.Headers.ContentType=new("image/png");form.Add(fake,"file","valid.png");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/api/operation/stock-adjustments/images",form)).StatusCode);
        var ok=await client.PostAsJsonAsync("/api/operation/stock-adjustments",request);Assert.Equal(HttpStatusCode.OK,ok.StatusCode);
    }
}
