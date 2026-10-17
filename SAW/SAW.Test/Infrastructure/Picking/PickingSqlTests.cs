using System.Data.Common;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.DistributorOrders;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.Picking;

public sealed class PickingSqlTests(PickingSqlFixture fixture) : IClassFixture<PickingSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Database;
    private IHttpContextAccessor Accessor => AccessorFor(F.ActorId);
    private static IHttpContextAccessor AccessorFor(int actorId) => new HttpContextAccessor { HttpContext = new DefaultHttpContext {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id",actorId.ToString()),new Claim(ClaimTypes.Role,"OPERATION_STAFF")],"test")) } };
    private DbContextOptions<AppDbContext> Options(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString,sql => sql.EnableRetryOnFailure(3,TimeSpan.FromMilliseconds(100),null));
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }
    private PickingRepository Repository(DbCommandInterceptor? interceptor = null, int? actorId = null) => new(Options(interceptor),AccessorFor(actorId ?? F.ActorId));
    private OrderReservationAllocator Allocator(DbCommandInterceptor? interceptor = null) => new(Options(interceptor),Accessor);
    private sealed record Scenario(long OrderId,long LineId,long BatchId,string Code,List<long> InventoryIds,int BuyerAccountId);
    private async Task<Scenario> ScenarioAsync(string status = "APPROVED", decimal[]? quantities = null)
    {
        quantities ??= [100];
        var batch = await F.BatchAsync();
        await using var db = F.Context();
        var stored = await db.ProductBatches.FindAsync(batch.ProductBatchId); stored!.BatchStatus="IN_STOCK";
        stored.QualityGrade="E"; stored.ExpiryDate=DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(30);
        var key=Guid.NewGuid().ToString("N");
        var account=new Account { RoleId=await db.Roles.Select(r=>r.RoleId).FirstAsync(),Username=key,Email=key+"@test.invalid",FullName="Picking buyer",PasswordHash="unused-test",AccountStatus="ACTIVE" };
        var buyer=new Distributor { Account=account,DistributorCode="P-"+key[..20],DistributorName="Picking distributor "+key[..5],TaxCode=key,ProfileStatus="ACTIVE" };
        var approved=status!="PENDING";
        var order=new PurchaseOrder { Distributor=buyer,OrderCode="PICK-"+key,OrderStatus=status,DeliveryAddress="Test address",OrderNote="Keep original note",
            SubtotalAmount=1234567,TotalAmount=1234567,ApprovedAt=approved?DateTime.UtcNow.AddMinutes(-1):null,
            ApprovedByAccountId=approved?F.ActorId:null,CreatedAt=DateTime.UtcNow.AddDays(-1),ExpectedDeliveryDate=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2) };
        var line=new OrderDetail { PurchaseOrder=order,CropTypeId=F.CropTypeId,RequestedProductBatchId=batch.ProductBatchId,
            RequestedQuantity=1,ApprovedQuantity=approved?1:0,RequestedWeightKg=100,ApprovedWeightKg=approved?100:0,Unit="Lô",UnitPrice=1234567 };
        var inventory=quantities.Select(q=>new Inventory {ProductBatchId=batch.ProductBatchId,QuantityOnHand=q,Unit="kg",LastUpdatedAt=DateTime.UtcNow,
            WarehouseLocation=new WarehouseLocation {LocationCode="P-"+Guid.NewGuid().ToString("N")[..16],ZoneName="Picking test",LocationStatus="ACTIVE"}}).ToList();
        db.Add(line);db.AddRange(inventory);await db.SaveChangesAsync();
        return new(order.PurchaseOrderId,line.OrderDetailId,batch.ProductBatchId,order.OrderCode,inventory.Select(i=>i.InventoryId).ToList(),account.AccountId);
    }
    private async Task<ConfirmPickingRequest> RequestAsync(Scenario scenario, decimal quantity, int reservationIndex=0)
    {
        var detail=await Repository().GetAsync(scenario.OrderId,default);
        var r=detail.Lines.Single().Reservations[reservationIndex];
        return new(Guid.NewGuid(),r.Id,scenario.LineId,r.InventoryId,scenario.BatchId,quantity,r.PickedQuantityKg,"Test picking");
    }
    private async Task<(decimal Hand,decimal Reserved,decimal Picked,int Histories,int Audits,string Status)> StateAsync(Scenario s)
    {
        await using var db=F.Context();
        return (await db.Inventories.Where(i=>i.ProductBatchId==s.BatchId).SumAsync(i=>i.QuantityOnHand),
            await db.Inventories.Where(i=>i.ProductBatchId==s.BatchId).SumAsync(i=>i.ReservedQuantity),
            await db.OrderDetails.Where(l=>l.PurchaseOrderId==s.OrderId).SumAsync(l=>l.PickedWeightKg),
            await db.PickingHistories.CountAsync(h=>h.InventoryReservation.OrderDetail.PurchaseOrderId==s.OrderId),
            await db.AuditLogs.CountAsync(a=>a.ActionType=="CONFIRM_PICKING"&&a.EntityName=="PURCHASE_ORDER"&&a.EntityId==s.OrderId.ToString()),
            await db.PurchaseOrders.Where(o=>o.PurchaseOrderId==s.OrderId).Select(o=>o.OrderStatus).SingleAsync());
    }
    [ReceivingSqlFact]
    public async Task Manager_approval_reserves_selected_whole_lot_once_without_changing_price_or_stock()
    {
        var s=await ScenarioAsync("PENDING",[40,60]);
        await using var managerDb=new AppDbContext(Options(),Accessor);
        var manager=new WarehouseInventoryRepository(managerDb,Allocator());
        var result=await manager.ApproveDistributorOrderAsync(F.ActorId,s.OrderId,default);
        Assert.Equal("RESERVED",result.Status);Assert.Equal(1234567,result.TotalAmount);
        Assert.True(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        Assert.Equal((100m,100m,0m,0,0,"RESERVED"),await StateAsync(s));
        await using var db=F.Context();
        Assert.Equal(2,await db.InventoryReservations.CountAsync(r=>r.OrderDetailId==s.LineId));
        Assert.Equal(1,await db.OrderDetails.Where(l=>l.OrderDetailId==s.LineId).Select(l=>l.ApprovedQuantity).SingleAsync());
        Assert.Equal("Keep original note",(await db.PurchaseOrders.FindAsync(s.OrderId))!.OrderNote);
        Assert.Single(await db.OrderStatusHistories.Where(h=>h.PurchaseOrderId==s.OrderId&&h.NewStatus=="RESERVED").ToListAsync());
        Assert.All(await db.OrderStatusHistories.Where(h=>h.PurchaseOrderId==s.OrderId&&(h.NewStatus=="APPROVED"||h.NewStatus=="RESERVED")).ToListAsync(),h=>Assert.Equal(F.ActorId,h.ChangedByAccountId));
    }
    [ReceivingSqlFact]
    public async Task Insufficient_allocation_stays_approved_with_no_partial_reservation_and_GET_never_allocates()
    {
        var s=await ScenarioAsync(quantities:[40,59]);
        Assert.False(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        var before=await StateAsync(s); var detail=await Repository().GetAsync(s.OrderId,default);
        Assert.False(detail.Order.CanPick);Assert.Contains("chờ phân bổ",detail.Order.PickingLockReason);
        Assert.Empty(detail.Lines.Single().Reservations);Assert.Equal(before,await StateAsync(s));
        Assert.NotNull(detail.Lines.Single().RequestedBatchCode);
        Assert.Single((await Repository().SearchAsync(new(Search:detail.Lines.Single().RequestedBatchCode),default)).Items);
        Assert.Equal((99m,0m,0m,0,0,"APPROVED"),before);
    }
    [ReceivingSqlFact]
    public async Task Retry_completes_existing_unpicked_allocation_without_duplicate_inventory_pair()
    {
        var s=await ScenarioAsync();
        await using(var setup=F.Context())
        {
            setup.InventoryReservations.Add(new InventoryReservation {OrderDetailId=s.LineId,InventoryId=s.InventoryIds[0],ReservedQuantity=20,ReservationStatus="RESERVED",ReservedAt=DateTime.UtcNow});
            await setup.SaveChangesAsync();
        }
        Assert.Equal((100m,20m,0m,0,0,"APPROVED"),await StateAsync(s));
        Assert.True(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        Assert.True(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        Assert.Equal((100m,100m,0m,0,0,"RESERVED"),await StateAsync(s));
        await using var verify=F.Context();
        Assert.Single(await verify.InventoryReservations.Where(r=>r.OrderDetailId==s.LineId).ToListAsync());
    }
    [ReceivingSqlFact]
    public async Task Partial_then_full_picking_uses_real_triggers_once_and_never_reduces_stock_or_releases_reservation()
    {
        var s=await ScenarioAsync();Assert.True(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        var first=await RequestAsync(s,40);await Repository().ConfirmAsync(s.OrderId,first,F.ActorId,default);
        var partial=await Repository().GetAsync(s.OrderId,default);
        Assert.Equal("PARTIALLY_PICKED",partial.Order.Status);Assert.Equal("PARTIALLY_PICKED",partial.Lines.Single().Reservations.Single().Status);
        Assert.Equal((100m,100m,40m,1,1,"PARTIALLY_PICKED"),await StateAsync(s));
        await Repository().ConfirmAsync(s.OrderId,first,F.ActorId,default);
        Assert.Equal((100m,100m,40m,1,1,"PARTIALLY_PICKED"),await StateAsync(s));
        var second=await RequestAsync(s,60);await Repository().ConfirmAsync(s.OrderId,second,F.ActorId,default);
        await Repository().ConfirmAsync(s.OrderId,second,F.ActorId,default);
        Assert.Equal((100m,100m,100m,2,2,"PICKED"),await StateAsync(s));
        await using var db=F.Context();
        Assert.Equal(0,await db.GoodsIssues.CountAsync(i=>i.PurchaseOrderId==s.OrderId));
        Assert.Equal(0,await db.StockAdjustments.CountAsync(a=>a.Inventory.ProductBatchId==s.BatchId));
        Assert.Equal("IN_STOCK",(await db.ProductBatches.FindAsync(s.BatchId))!.BatchStatus);
        Assert.Single(await db.OrderStatusHistories.Where(h=>h.PurchaseOrderId==s.OrderId&&h.NewStatus=="PICKED").ToListAsync());
    }
    [ReceivingSqlFact]
    public async Task Multiple_locations_only_finish_order_when_every_active_reservation_is_picked()
    {
        var s=await ScenarioAsync(quantities:[40,60]);Assert.True(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        await Repository().ConfirmAsync(s.OrderId,await RequestAsync(s,40),F.ActorId,default);
        var d=await Repository().GetAsync(s.OrderId,default);Assert.Equal("PARTIALLY_PICKED",d.Order.Status);
        Assert.Equal(new[]{"PICKED","RESERVED"},d.Lines.Single().Reservations.Select(r=>r.Status));
        await Repository().ConfirmAsync(s.OrderId,await RequestAsync(s,60,1),F.ActorId,default);
        Assert.Equal((100m,100m,100m,2,2,"PICKED"),await StateAsync(s));
    }
    [ReceivingSqlFact]
    public async Task Kg_precision_note_boundaries_and_original_supplier_units_are_preserved()
    {
        var s=await ScenarioAsync();Assert.True(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        var service=new PickingService(Repository());
        var request=(await RequestAsync(s,0.001m)) with {Note=" "+new string('n',1000)+" "};
        var detail=await service.ConfirmAsync(s.OrderId,request,F.ActorId,default);
        Assert.Equal(0.001m,detail.Order.PickedWeightKg);Assert.Equal(1000,detail.Lines.Single().Reservations.Single().History.Single().Note!.Length);
        await service.ConfirmAsync(s.OrderId,(await RequestAsync(s,99.999m)) with {Note="  "},F.ActorId,default);
        Assert.Equal((100m,100m,100m,2,2,"PICKED"),await StateAsync(s));
        await using var db=F.Context();var batch=await db.ProductBatches.FindAsync(s.BatchId);
        Assert.Equal("Box",batch!.Unit);Assert.Equal(10,batch.DeclaredQuantity);Assert.Equal(100,batch.WeightInKg);
        Assert.Equal("Supplier note",batch.Note);Assert.Equal("Lô",(await db.OrderDetails.FindAsync(s.LineId))!.Unit);
        Assert.Null((await db.PickingHistories.Where(h=>h.InventoryReservation.OrderDetailId==s.LineId).OrderByDescending(h=>h.PickingHistoryId).FirstAsync()).Note);
    }
    [ReceivingSqlFact]
    public async Task Over_quantity_old_snapshot_foreign_reservation_and_changed_retry_payload_do_not_write()
    {
        var s=await ScenarioAsync();var other=await ScenarioAsync();
        await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default);await Allocator().TryReserveAsync(other.OrderId,F.ActorId,default);
        var req=await RequestAsync(s,20);var before=await StateAsync(s);
        var foreign=await RequestAsync(other,1);
        await Assert.ThrowsAsync<ConflictException>(()=>Repository().ConfirmAsync(s.OrderId,req with{QuantityKg=101},F.ActorId,default));
        await Assert.ThrowsAsync<ConflictException>(()=>Repository().ConfirmAsync(s.OrderId,foreign,F.ActorId,default));
        Assert.Equal(before,await StateAsync(s));
        await Repository().ConfirmAsync(s.OrderId,req,F.ActorId,default);
        var after=await StateAsync(s);
        await Assert.ThrowsAsync<ConflictException>(()=>Repository().ConfirmAsync(s.OrderId,req with{RequestId=Guid.NewGuid()},F.ActorId,default));
        await Assert.ThrowsAsync<ConflictException>(()=>Repository().ConfirmAsync(s.OrderId,req with{QuantityKg=21},F.ActorId,default));
        Assert.Equal(after,await StateAsync(s));
    }
    [ReceivingSqlFact]
    public async Task Expired_blocked_batch_and_inactive_location_reject_picking_without_any_write()
    {
        foreach(var kind in new[]{"expired","QUARANTINE","REJECTED","CANCELLED","location"})
        {
            var s=await ScenarioAsync();await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default);var req=await RequestAsync(s,1);
            await using(var setup=F.Context())
            {
                var b=await setup.ProductBatches.FindAsync(s.BatchId);
                if(kind=="expired")b!.ExpiryDate=DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(-1);
                else if(kind=="location") {var i=await setup.Inventories.Include(x=>x.WarehouseLocation).SingleAsync(x=>x.InventoryId==s.InventoryIds[0]);i.WarehouseLocation.LocationStatus="INACTIVE";}
                else b!.BatchStatus=kind;
                await setup.SaveChangesAsync();
            }
            var before=await StateAsync(s);
            var error=await Assert.ThrowsAsync<ConflictException>(()=>Repository().ConfirmAsync(s.OrderId,req,F.ActorId,default));
            Assert.Contains(kind=="expired"?"hết hạn":kind=="location"?"Vị trí":"Trạng thái lô",error.Message);
            Assert.Equal(before,await StateAsync(s));
        }
    }
    [ReceivingSqlFact]
    public async Task Concurrent_stale_picking_only_one_wins_and_same_request_replay_is_idempotent()
    {
        var s=await ScenarioAsync();await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default);var first=await RequestAsync(s,70);
        int otherActor;
        await using(var setup=F.Context())
        {
            var key=Guid.NewGuid().ToString("N");
            var account=new Account{RoleId=await setup.Roles.Select(r=>r.RoleId).FirstAsync(),Username=key,Email=key+"@test.invalid",FullName="Second picking staff",PasswordHash="unused-fixture",AccountStatus="ACTIVE"};
            setup.Accounts.Add(account);await setup.SaveChangesAsync();otherActor=account.AccountId;
        }
        async Task<bool> Attempt(ConfirmPickingRequest r,int actor){try{await Repository(actorId:actor).ConfirmAsync(s.OrderId,r,actor,default);return true;}catch(ConflictException){return false;}}
        var results=await Task.WhenAll(Attempt(first,F.ActorId),Attempt(first with{RequestId=Guid.NewGuid()},otherActor));
        Assert.Equal(1,results.Count(x=>x));Assert.Equal((100m,100m,70m,1,1,"PARTIALLY_PICKED"),await StateAsync(s));
        await using(var verify=F.Context())
        {
            var pickedBy=await verify.PickingHistories.Where(h=>h.InventoryReservation.OrderDetailId==s.LineId).Select(h=>h.PickedByAccountId).SingleAsync();
            Assert.Equal(results[0]?F.ActorId:otherActor,pickedBy);
            Assert.Equal(pickedBy,await verify.AuditLogs.Where(a=>a.ActionType=="CONFIRM_PICKING"&&a.EntityId==s.OrderId.ToString()).Select(a=>a.AccountId).SingleAsync());
        }
        var s2=await ScenarioAsync();await Allocator().TryReserveAsync(s2.OrderId,F.ActorId,default);var same=await RequestAsync(s2,100);
        await Task.WhenAll(Repository().ConfirmAsync(s2.OrderId,same,F.ActorId,default),Repository().ConfirmAsync(s2.OrderId,same,F.ActorId,default));
        Assert.Equal((100m,100m,100m,1,1,"PICKED"),await StateAsync(s2));
    }
    [ReceivingSqlFact]
    public async Task Concurrent_allocation_and_confirm_retries_never_double_reserve_or_pick()
    {
        var s=await ScenarioAsync();var result=await Task.WhenAll(Allocator().TryReserveAsync(s.OrderId,F.ActorId,default),Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        Assert.All(result,Assert.True);Assert.Equal((100m,100m,0m,0,0,"RESERVED"),await StateAsync(s));
    }
    [ReceivingSqlFact]
    public async Task Two_approved_orders_cannot_reserve_the_same_inventory_twice()
    {
        var first=await ScenarioAsync();var second=await ScenarioAsync();
        await using(var setup=F.Context()) { var line=await setup.OrderDetails.FindAsync(second.LineId);line!.RequestedProductBatchId=first.BatchId;await setup.SaveChangesAsync(); }
        var results=await Task.WhenAll(Allocator().TryReserveAsync(first.OrderId,F.ActorId,default),Allocator().TryReserveAsync(second.OrderId,F.ActorId,default));
        Assert.Equal(1,results.Count(x=>x));
        await using var db=F.Context();
        Assert.Equal(100,await db.Inventories.Where(i=>i.ProductBatchId==first.BatchId).SumAsync(i=>i.ReservedQuantity));
        Assert.Equal(100,await db.InventoryReservations.Where(r=>r.Inventory.ProductBatchId==first.BatchId).SumAsync(r=>r.ReservedQuantity));
    }
    private sealed class FailWrite(string marker):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if((marker=="reserve"&&command.CommandText.Contains("INSERT INTO [INVENTORY_RESERVATION]"))
                ||(marker=="audit"&&command.Parameters.Cast<DbParameter>().Any(p=>p.Value is string value&&value=="CONFIRM_PICKING")))
                throw new InvalidOperationException("Isolated picking persistence fault");
            return ValueTask.FromResult(result);
        }
    }
    [ReceivingSqlFact]
    public async Task Persistence_failure_rolls_back_picking_and_reservations_but_keeps_committed_approval()
    {
        var s=await ScenarioAsync();await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default);var req=await RequestAsync(s,20);var before=await StateAsync(s);
        await Assert.ThrowsAsync<DbUpdateException>(()=>Repository(new FailWrite("audit")).ConfirmAsync(s.OrderId,req,F.ActorId,default));
        Assert.Equal(before,await StateAsync(s));
        var pending=await ScenarioAsync("PENDING",[40,60]);
        await using(var managerDb=new AppDbContext(Options(),Accessor))
        {
            var manager=new WarehouseInventoryRepository(managerDb,Allocator(new FailWrite("reserve")));
            await Assert.ThrowsAsync<DbUpdateException>(()=>manager.ApproveDistributorOrderAsync(F.ActorId,pending.OrderId,default));
        }
        Assert.Equal((100m,0m,0m,0,0,"APPROVED"),await StateAsync(pending));
        Assert.True(await Allocator().TryReserveAsync(pending.OrderId,F.ActorId,default));
    }
    [ReceivingSqlFact]
    public async Task List_filters_sort_pages_exclude_completed_and_GET_preserves_all_saved_data()
    {
        var samples=new List<Scenario>();for(var i=0;i<4;i++)samples.Add(await ScenarioAsync());
        var first=samples[0];await Allocator().TryReserveAsync(first.OrderId,F.ActorId,default);
        var closed=samples[1];await Allocator().TryReserveAsync(closed.OrderId,F.ActorId,default);await Repository().ConfirmAsync(closed.OrderId,await RequestAsync(closed,100),F.ActorId,default);
        var prefix="LIST-"+Guid.NewGuid().ToString("N")[..10];var time=DateTime.UtcNow.AddMinutes(-3);
        await using(var setup=F.Context()){for(var i=0;i<samples.Count;i++){var o=await setup.PurchaseOrders.FindAsync(samples[i].OrderId);o!.OrderCode=prefix+"-"+i;o.ApprovedAt=time;}await setup.SaveChangesAsync();}
        var before=await StateAsync(first);await using var snapshot=F.Context();var audits=await snapshot.AuditLogs.CountAsync();
        var page1=await Repository().SearchAsync(new(Search:prefix,Sort:"approved_asc",PageSize:1),default);
        var page2=await Repository().SearchAsync(new(Search:prefix,Sort:"approved_asc",Page:2,PageSize:1),default);
        Assert.Equal(3,page1.TotalCount);Assert.Equal(samples[0].OrderId,page1.Items.Single().Id);Assert.Equal(samples[2].OrderId,page2.Items.Single().Id);
        Assert.Single((await Repository().SearchAsync(new(Search:prefix,Status:"RESERVED"),default)).Items);
        Assert.Empty((await Repository().SearchAsync(new(Search:prefix,FromDate:DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5)),default)).Items);
        Assert.Empty((await Repository().SearchAsync(new(Search:Guid.NewGuid().ToString()),default)).Items);
        await Repository().GetAsync(first.OrderId,default);Assert.Equal(before,await StateAsync(first));Assert.Equal(audits,await snapshot.AuditLogs.CountAsync());
    }
    private string Token(string role,int? accountId=null)=>new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test","uc28-test",
        [new Claim("account_id",(accountId??F.ActorId).ToString()),new Claim(ClaimTypes.Role,role)],expires:DateTime.UtcNow.AddMinutes(20),
        signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)),SecurityAlgorithms.HmacSha256)));
    [DistributorBrowserFact]
    public async Task Browser_staff_list_partial_complete_stale_expired_and_manager_allocation_retry()
    {
        var main=await ScenarioAsync();var waiting=await ScenarioAsync(quantities:[99]);var expired=await ScenarioAsync();var stale=await ScenarioAsync();
        foreach(var s in new[]{main,expired,stale})Assert.True(await Allocator().TryReserveAsync(s.OrderId,F.ActorId,default));
        await using(var setup=F.Context()){var b=await setup.ProductBatches.FindAsync(expired.BatchId);b!.ExpiryDate=DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(-1);await setup.SaveChangesAsync();}
        using var api=await F.ApiAsync();var root=new DirectoryInfo(AppContext.BaseDirectory);
        api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token("OPERATION_STAFF"));
        var apiList=await api.GetAsync("/api/operation/picking-orders");Assert.Equal(HttpStatusCode.OK,apiList.StatusCode);
        while(root is not null&&!Directory.Exists(Path.Combine(root.FullName,"Capstone_SAW_FE")))root=root.Parent;
        Assert.NotNull(root);
        var start=new ProcessStartInfo("node"){WorkingDirectory=Path.Combine(root.FullName,"Capstone_SAW_FE"),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("tests/picking-orders-browser.mjs");
        start.Environment["SAW_TEST_API_URL"]=api.BaseAddress!.ToString();
        start.Environment["SAW_TEST_ORDER_ID"]=main.OrderId.ToString();start.Environment["SAW_TEST_WAITING_ORDER_ID"]=waiting.OrderId.ToString();
        start.Environment["SAW_TEST_EXPIRED_ORDER_ID"]=expired.OrderId.ToString();start.Environment["SAW_TEST_STALE_ORDER_ID"]=stale.OrderId.ToString();
        start.Environment["SAW_TEST_ORDER_CODE"]=main.Code;start.Environment["SAW_TEST_TOKEN"]=Token("OPERATION_STAFF");start.Environment["SAW_TEST_MANAGER_TOKEN"]=Token("WAREHOUSE_MANAGER");
        start.Environment["SAW_TEST_BUYER_TOKEN"]=Token("DISTRIBUTOR",main.BuyerAccountId);
        var evidence=Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY")??Path.Combine(root.FullName,"Capstone_SAW_BE","test-results","picking");Directory.CreateDirectory(evidence);
        start.Environment["SAW_TEST_EVIDENCE_DIRECTORY"]=evidence;
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token);}catch{process.Kill(entireProcessTree:true);throw;}
        var log=await output+await error;await File.WriteAllTextAsync(Path.Combine(evidence,"picking-browser.log"),log);Assert.True(process.ExitCode==0,log);
        Assert.Equal((100m,100m,100m,2,2,"PICKED"),await StateAsync(main));
        Assert.Equal((99m,0m,0m,0,0,"APPROVED"),await StateAsync(waiting));
        Assert.Equal((100m,100m,0m,0,0,"RESERVED"),await StateAsync(expired));
        Assert.Equal((100m,100m,10m,1,1,"PARTIALLY_PICKED"),await StateAsync(stale));
    }
    [ReceivingSqlFact]
    public async Task HTTP_roles_validation_spoofing_and_waiting_allocation_are_enforced_by_backend()
    {
        var s=await ScenarioAsync();using var api=await F.ApiAsync();var path="/api/operation/picking-orders";
        Assert.Equal(HttpStatusCode.Unauthorized,(await api.GetAsync(path)).StatusCode);
        foreach(var role in new[]{"WAREHOUSE_MANAGER","QC_STAFF","ADMINISTRATOR","SUPPLIER","DISTRIBUTOR"})
        {
            api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token(role));
            Assert.Equal(HttpStatusCode.Forbidden,(await api.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await api.GetAsync(path+"/"+s.OrderId)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await api.PostAsJsonAsync(path+"/"+s.OrderId+"/confirm-picking",new{})).StatusCode);
        }
        api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token("OPERATION_STAFF"));
        Assert.Equal(HttpStatusCode.OK,(await api.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await api.GetAsync(path+"/"+s.OrderId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await api.GetAsync(path+"/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await api.GetAsync(path+"/999999999")).StatusCode);
        foreach(var q in new[]{"page=0","pageSize=101","status=PICKED","sort=bad","fromDate=2026-12-31&toDate=2026-01-01","search="+new string('x',101),"toDate=9999-12-31","fromDate=0001-01-01"})
            Assert.Equal(HttpStatusCode.BadRequest,(await api.GetAsync(path+"?"+q)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await api.PostAsync($"/api/warehouse-manager/orders/{s.OrderId}/reserve",null)).StatusCode);
        var unallocated=new ConfirmPickingRequest(Guid.NewGuid(),1,s.LineId,s.InventoryIds[0],s.BatchId,1,0,null);
        Assert.Equal(HttpStatusCode.Conflict,(await api.PostAsJsonAsync(path+"/"+s.OrderId+"/confirm-picking",unallocated)).StatusCode);
        Assert.Equal((100m,0m,0m,0,0,"APPROVED"),await StateAsync(s));
        api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token("WAREHOUSE_MANAGER"));
        Assert.Equal(HttpStatusCode.OK,(await api.PostAsync($"/api/warehouse-manager/orders/{s.OrderId}/reserve",null)).StatusCode);
        api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token("OPERATION_STAFF"));var req=await RequestAsync(s,10);
        foreach(var field in new[]{"pickedByAccountId","orderStatus","pickedQuantity","pickedAt","role"})
        {
            var body=JsonSerializer.SerializeToNode(req,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();body[field]="forged";
            Assert.Equal(HttpStatusCode.BadRequest,(await api.PostAsJsonAsync(path+"/"+s.OrderId+"/confirm-picking",body)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK,(await api.PostAsJsonAsync(path+"/"+s.OrderId+"/confirm-picking",req)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await api.PostAsJsonAsync(path+"/"+s.OrderId+"/confirm-picking",req)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await api.PostAsJsonAsync(path+"/"+s.OrderId+"/confirm-picking",req with{RequestId=Guid.NewGuid()})).StatusCode);
        Assert.Equal((100m,100m,10m,1,1,"PARTIALLY_PICKED"),await StateAsync(s));
    }
}
