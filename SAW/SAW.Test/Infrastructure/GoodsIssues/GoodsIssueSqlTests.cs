using System.Data.Common;
using System.Diagnostics;
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
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsIssues;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;
using SAW.Test.Infrastructure.DistributorOrders;
using IssueDetail = SAW.Application.Features.GoodsIssues.GoodsIssueDetail;

namespace SAW.Test.Infrastructure.GoodsIssues;

public sealed class GoodsIssueSqlTests(GoodsIssueSqlFixture fixture) : IClassFixture<GoodsIssueSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Picking.Database;
    private IHttpContextAccessor Accessor => new HttpContextAccessor { HttpContext = new DefaultHttpContext {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", F.ActorId.ToString()), new Claim(ClaimTypes.Role, "OPERATION_STAFF")], "test")) } };
    private DbContextOptions<AppDbContext> Options(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null));
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }
    private GoodsIssueService Service(DbCommandInterceptor? interceptor = null) => new(new GoodsIssueRepository(Options(interceptor), Accessor));
    private PickingService Picking() => new(new PickingRepository(Options(), Accessor));
    private sealed record Scenario(long OrderId, long LineId, long BatchId, string Code, List<long> InventoryIds);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
    private async Task<Scenario> ScenarioAsync(decimal pick = 100, decimal[]? quantities = null)
    {
        quantities ??= [100]; var batch = await F.BatchAsync();
        await using var db = F.Context(); var b = await db.ProductBatches.FindAsync(batch.ProductBatchId);
        b!.BatchStatus = "IN_STOCK"; b.QualityGrade = "E"; b.ExpiryDate = Today.AddDays(30);
        var key = Guid.NewGuid().ToString("N");
        var buyer = new Distributor { Account = new Account { RoleId = await db.Roles.Select(r => r.RoleId).FirstAsync(), Username = key,
            Email = key + "@test.invalid", FullName = "Issue buyer", PasswordHash = "unused-test", AccountStatus = "ACTIVE" },
            DistributorCode = "I-" + key[..20], DistributorName = "Issue distributor " + key[..5], TaxCode = key, ProfileStatus = "ACTIVE" };
        var order = new PurchaseOrder { Distributor = buyer, OrderCode = "ISSUE-" + key, OrderStatus = "APPROVED", DeliveryAddress = "Test address",
            SubtotalAmount = 2000000, TotalAmount = 2000000, ApprovedAt = DateTime.UtcNow.AddMinutes(-1), ApprovedByAccountId = F.ActorId, CreatedAt = DateTime.UtcNow.AddDays(-1) };
        var line = new OrderDetail { PurchaseOrder = order, CropTypeId = F.CropTypeId, RequestedProductBatchId = b.ProductBatchId,
            RequestedQuantity = 1, ApprovedQuantity = 1, RequestedWeightKg = 100, ApprovedWeightKg = 100, Unit = "Lô", UnitPrice = 2000000 };
        var stocks = quantities.Select(q => new Inventory { ProductBatchId = b.ProductBatchId, QuantityOnHand = q, Unit = "kg", LastUpdatedAt = DateTime.UtcNow,
            WarehouseLocation = new WarehouseLocation { LocationCode = "I-" + Guid.NewGuid().ToString("N")[..16], ZoneName = "Issue test", LocationStatus = "ACTIVE" } }).ToList();
        db.Add(line); db.AddRange(stocks); await db.SaveChangesAsync();
        Assert.True(await new OrderReservationAllocator(Options(), Accessor).TryReserveAsync(order.PurchaseOrderId, F.ActorId, default));
        var scenario = new Scenario(order.PurchaseOrderId, line.OrderDetailId, b.ProductBatchId, order.OrderCode, stocks.Select(i => i.InventoryId).ToList());
        if (pick > 0)
        {
            var detail = await Picking().GetAsync(scenario.OrderId, default);
            foreach (var r in detail.Lines.Single().Reservations)
            {
                var amount = Math.Min(pick, r.ReservedQuantityKg);
                if (amount > 0) await Picking().ConfirmAsync(scenario.OrderId, new(Guid.NewGuid(), r.Id, line.OrderDetailId, r.InventoryId, b.ProductBatchId, amount, 0, "Fixture picking"), F.ActorId, default);
                pick -= amount;
            }
        }
        return scenario;
    }
    private async Task<CreateGoodsIssueRequest> RequestAsync(Scenario s, decimal quantity = 100)
    {
        var data = await Service().OrderAsync(s.OrderId, default);
        return new(Guid.NewGuid(), s.OrderId, Today, " Test receiver ", " Test note ", [new(data.Allocations.First().ReservationId, quantity)]);
    }
    private async Task<string> StockStateAsync(Scenario s)
    {
        await using var db = F.Context();
        var stocks = await db.Inventories.AsNoTracking().Where(i => i.ProductBatchId == s.BatchId).OrderBy(i => i.InventoryId)
            .Select(i => new { i.QuantityOnHand, i.ReservedQuantity, i.AvailableQuantity }).ToListAsync();
        var reservations = await db.InventoryReservations.AsNoTracking().Where(r => r.OrderDetailId == s.LineId).OrderBy(r => r.InventoryReservationId)
            .Select(r => new { r.ReservedQuantity, r.PickedQuantity, r.ReservationStatus, r.ReleasedAt }).ToListAsync();
        var order = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == s.OrderId).Select(o => new { o.OrderStatus, o.TotalAmount }).SingleAsync();
        var batch = await db.ProductBatches.Where(b => b.ProductBatchId == s.BatchId).Select(b => new { b.BatchStatus, b.QualityGrade }).SingleAsync();
        return JsonSerializer.Serialize(new { stocks, reservations, order, batch, Picks = await db.PickingHistories.CountAsync(h => h.InventoryReservation.OrderDetailId == s.LineId) });
    }
    private static ConfirmGoodsIssueRequest Confirm(IssueDetail detail) => new(Guid.NewGuid(), detail.Snapshot);
    [ReceivingSqlFact]
    public async Task Draft_edit_and_GET_preserve_stock_and_fixed_fields_then_confirm_consumes_exactly_once()
    {
        var s = await ScenarioAsync(); var input = await RequestAsync(s); var before = await StockStateAsync(s);
        var draft = await Service().CreateAsync(input, F.ActorId, default);
        Assert.Equal(before, await StockStateAsync(s)); Assert.Equal("DRAFT", draft.Summary.IssueStatus);
        Assert.Equal(draft.Summary.Id, (await Service().CreateAsync(input, F.ActorId, default)).Summary.Id);
        Assert.Equal("Test receiver", draft.ReceiverName); Assert.Equal("Test note", draft.Note);
        var edited = await Service().UpdateDraftAsync(draft.Summary.Id, new(Guid.NewGuid(), draft.Snapshot, Today.AddDays(-1), null, null, [new(input.Lines[0].ReservationId, 100)]), F.ActorId, default);
        Assert.Equal(before, await StockStateAsync(s)); Assert.NotEqual(draft.Snapshot, edited.Snapshot);
        Assert.Null(edited.Note); Assert.Null(edited.ReceiverName); Assert.Equal(draft.Summary.IssueCode, edited.Summary.IssueCode);
        Assert.Equal(draft.Summary.OperationStaffName, edited.Summary.OperationStaffName); Assert.Equal(s.OrderId, edited.Summary.PurchaseOrderId);
        await using var db = F.Context(); var audits = await db.AuditLogs.CountAsync();
        var list = await Service().SearchAsync(new(Search: s.Code), default); Assert.Contains(list.Items, g => g.Id == draft.Summary.Id);
        Assert.Equal(edited.Snapshot, (await Service().GetAsync(draft.Summary.Id, default)).Snapshot);
        Assert.Equal(audits, await db.AuditLogs.CountAsync());
        var request = Confirm(edited); var committed = await Service().ConfirmAsync(edited.Summary.Id, request, F.ActorId, default);
        Assert.Equal("COMMITTED", committed.Summary.IssueStatus); Assert.NotNull(committed.CommittedAt); Assert.False(committed.CanEdit);
        var after = await StockStateAsync(s); await Service().ConfirmAsync(edited.Summary.Id, request, F.ActorId, default); Assert.Equal(after, await StockStateAsync(s));
        var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductBatchId == s.BatchId);
        Assert.Equal(0, inv.QuantityOnHand); Assert.Equal(0, inv.ReservedQuantity); Assert.Equal(0, inv.AvailableQuantity);
        Assert.Equal("ISSUED", await db.PurchaseOrders.Where(o => o.PurchaseOrderId == s.OrderId).Select(o => o.OrderStatus).SingleAsync());
        Assert.Equal("ISSUED", await db.ProductBatches.Where(b => b.ProductBatchId == s.BatchId).Select(b => b.BatchStatus).SingleAsync());
        Assert.Single(committed.History, h => h.Action == "CONFIRM_GOODS_ISSUE");
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.ActionType == "CONFIRM_GOODS_ISSUE" && a.EntityId == committed.Summary.Id.ToString());
        Assert.Equal(F.ActorId, audit.AccountId);
        using (var oldValues = JsonDocument.Parse(audit.OldDataJson!))
            Assert.Equal(100, oldValues.RootElement.GetProperty("Inventory")[0].GetProperty("QuantityOnHand").GetDecimal());
        using (var newValues = JsonDocument.Parse(audit.NewDataJson!))
        {
            var values = newValues.RootElement.GetProperty("Values");
            Assert.Equal(0, values.GetProperty("Inventory")[0].GetProperty("QuantityOnHand").GetDecimal());
            Assert.Equal("ISSUED", values.GetProperty("Reservations")[0].GetProperty("ReservationStatus").GetString());
        }
        Assert.Equal(1, await db.PickingHistories.CountAsync(h => h.InventoryReservation.OrderDetailId == s.LineId));
        Assert.Empty(await db.GoodsReceipts.Where(r => r.ProductBatchId == s.BatchId).ToListAsync());
    }
    [ReceivingSqlFact]
    public async Task Partial_issue_keeps_remaining_hold_then_picking_and_second_issue_finish_without_releasing_others()
    {
        var s = await ScenarioAsync(40); var input = await RequestAsync(s, 25); var draft = await Service().CreateAsync(input, F.ActorId, default);
        await Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default);
        await using var db = F.Context(); var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductBatchId == s.BatchId);
        Assert.Equal(75, inv.QuantityOnHand); Assert.Equal(75, inv.ReservedQuantity);
        var pick = await Picking().GetAsync(s.OrderId, default); Assert.True(pick.Order.CanPick);
        var r = pick.Lines.Single().Reservations.Single();
        await Picking().ConfirmAsync(s.OrderId, new(Guid.NewGuid(), r.Id, s.LineId, r.InventoryId, s.BatchId, 60, 40, null), F.ActorId, default);
        var data = await Service().OrderAsync(s.OrderId, default); Assert.Equal(75, data.Allocations.Single().RemainingWeightKg);
        Assert.Equal("PARTIALLY_ISSUED", (await Picking().GetAsync(s.OrderId, default)).Order.Status);
        var second = await Service().CreateAsync(await RequestAsync(s, 75), F.ActorId, default);
        await Service().ConfirmAsync(second.Summary.Id, Confirm(second), F.ActorId, default);
        inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductBatchId == s.BatchId);
        Assert.Equal(0, inv.QuantityOnHand); Assert.Equal(0, inv.ReservedQuantity);
        Assert.Empty((await Service().EligibleAsync(s.Code, 1, 20, default)).Items);
        Assert.Empty((await new PickingRepository(Options(), Accessor).SearchAsync(new(Search: s.Code), default)).Items);
    }
    [ReceivingSqlFact]
    public async Task Multiple_positions_and_duplicate_drafts_only_committed_counts_and_concurrent_confirm_cannot_overissue()
    {
        var s = await ScenarioAsync(100, [40, 60]); var order = await Service().OrderAsync(s.OrderId, default);
        var input = new CreateGoodsIssueRequest(Guid.NewGuid(), s.OrderId, Today, null, null, order.Allocations.Select(a => new GoodsIssueLineRequest(a.ReservationId, a.RemainingWeightKg)).ToList());
        var one = await Service().CreateAsync(input, F.ActorId, default);
        var two = await Service().CreateAsync(input with { RequestId = Guid.NewGuid() }, F.ActorId, default);
        var results = await Task.WhenAll(Try(() => Service().ConfirmAsync(one.Summary.Id, Confirm(one), F.ActorId, default)), Try(() => Service().ConfirmAsync(two.Summary.Id, Confirm(two), F.ActorId, default)));
        Assert.Equal(1, results.Count(r => r is null)); Assert.IsType<ConflictException>(results.Single(r => r is not null));
        await using var db = F.Context(); Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == s.BatchId).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == s.BatchId).SumAsync(i => i.ReservedQuantity));
        Assert.Equal(1, await db.GoodsIssues.CountAsync(g => g.PurchaseOrderId == s.OrderId && g.IssueStatus == "COMMITTED"));
    }
    [ReceivingSqlFact]
    public async Task Snapshot_conflict_edit_confirm_both_orders_and_retry_changed_payload()
    {
        var s = await ScenarioAsync(); var input = await RequestAsync(s); var draft = await Service().CreateAsync(input, F.ActorId, default);
        await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(input with { Note = "Changed replay" }, F.ActorId, default));
        var update = new UpdateGoodsIssueDraftRequest(Guid.NewGuid(), draft.Snapshot, Today, null, "Edited", input.Lines);
        var newer = await Service().UpdateDraftAsync(draft.Summary.Id, update, F.ActorId, default);
        Assert.Equal(newer.Snapshot, (await Service().UpdateDraftAsync(draft.Summary.Id, update, F.ActorId, default)).Snapshot);
        await Assert.ThrowsAsync<ConflictException>(() => Service().UpdateDraftAsync(draft.Summary.Id, update with { RequestId = Guid.NewGuid() }, F.ActorId, default));
        await Assert.ThrowsAsync<ConflictException>(() => Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default));
        await Service().ConfirmAsync(newer.Summary.Id, Confirm(newer), F.ActorId, default);
        await Assert.ThrowsAsync<ConflictException>(() => Service().UpdateDraftAsync(newer.Summary.Id, update with { RequestId = Guid.NewGuid(), ExpectedSnapshot = newer.Snapshot }, F.ActorId, default));
        await using var db = F.Context();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GOODS_ISSUE_DETAIL WHERE GoodsIssueID={newer.Summary.Id}"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE GOODS_ISSUE SET Note=N'changed' WHERE GoodsIssueID={newer.Summary.Id}"));
    }
    [ReceivingSqlFact]
    public async Task Concurrent_same_confirm_and_same_create_request_have_one_effect()
    {
        var s = await ScenarioAsync(); var input = await RequestAsync(s);
        var drafts = await Task.WhenAll(Service().CreateAsync(input, F.ActorId, default), Service().CreateAsync(input, F.ActorId, default));
        Assert.Equal(drafts[0].Summary.Id, drafts[1].Summary.Id);
        var request = Confirm(drafts[0]);
        await Task.WhenAll(Service().ConfirmAsync(drafts[0].Summary.Id, request, F.ActorId, default), Service().ConfirmAsync(drafts[0].Summary.Id, request, F.ActorId, default));
        await using var db = F.Context(); Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == s.BatchId).SumAsync(i => i.QuantityOnHand));
        Assert.Single(await db.AuditLogs.Where(a => a.ActionType == "CONFIRM_GOODS_ISSUE" && a.EntityId == drafts[0].Summary.Id.ToString()).ToListAsync());
        Assert.Single(await db.OrderStatusHistories.Where(h => h.PurchaseOrderId == s.OrderId && h.NewStatus == "ISSUED").ToListAsync());
    }
    [ReceivingSqlFact]
    public async Task Unpicked_foreign_reservation_and_approved_or_stock_bounds_are_rejected()
    {
        var unpicked = await ScenarioAsync(0); var input = await RequestAsync(unpicked);
        await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(input, F.ActorId, default));
        var s = await ScenarioAsync(); var foreign = await ScenarioAsync(); input = await RequestAsync(s);
        var foreignInput = await RequestAsync(foreign);
        await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(input with { Lines = foreignInput.Lines }, F.ActorId, default));
        await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(input with { Lines = [new(input.Lines[0].ReservationId, 100.001m)] }, F.ActorId, default));
        await using var db = F.Context();
        // The real check constraint also prevents approval from falling below already allocated/picked kg.
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ORDER_DETAIL SET ApprovedWeightKg=90 WHERE OrderDetailID={s.LineId}"));
        Assert.Equal(100, await db.OrderDetails.Where(l => l.OrderDetailId == s.LineId).Select(l => l.ApprovedWeightKg).SingleAsync());
    }
    [ReceivingSqlFact]
    public async Task Expiry_batch_location_and_order_changes_after_draft_are_rechecked_without_writes()
    {
        foreach (var condition in new[] { "EXPIRED", "QUARANTINE", "REJECTED", "CANCELLED", "LOCATION", "ORDER" })
        {
            var s = await ScenarioAsync(); var draft = await Service().CreateAsync(await RequestAsync(s), F.ActorId, default);
            await using var db = F.Context();
            if (condition == "EXPIRED") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PRODUCT_BATCH SET ExpiryDate={Today.AddDays(-1)} WHERE ProductBatchID={s.BatchId}");
            else if (condition == "LOCATION") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WAREHOUSE_LOCATION SET LocationStatus=N'INACTIVE' WHERE WarehouseLocationID=(SELECT WarehouseLocationID FROM INVENTORY WHERE InventoryID={s.InventoryIds[0]})");
            else if (condition == "ORDER") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PURCHASE_ORDER SET OrderStatus=N'CANCELLED' WHERE PurchaseOrderID={s.OrderId}");
            else await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PRODUCT_BATCH SET BatchStatus={condition} WHERE ProductBatchID={s.BatchId}");
            var before = await StockStateAsync(s); var audits = await db.AuditLogs.CountAsync();
            await Assert.ThrowsAsync<ConflictException>(() => Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default));
            Assert.Equal(before, await StockStateAsync(s)); Assert.Equal(audits, await db.AuditLogs.CountAsync());
            Assert.Equal("DRAFT", (await Service().GetAsync(draft.Summary.Id, default)).Summary.IssueStatus);
        }
    }
    private sealed class AuditFailure : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, "CONFIRM_GOODS_ISSUE"))) throw new InvalidOperationException("Fixture audit failure");
            return base.ReaderExecutingAsync(command, data, result, ct);
        }
    }
    [ReceivingSqlFact]
    public async Task Failed_audit_rolls_back_commit_inventory_reservations_order_batch_and_history()
    {
        var s = await ScenarioAsync(); var draft = await Service().CreateAsync(await RequestAsync(s), F.ActorId, default);
        var before = await StockStateAsync(s); await using var db = F.Context(); var auditCount = await db.AuditLogs.CountAsync();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Service(new AuditFailure()).ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default));
        Assert.Contains("Fixture audit failure", failure.ToString());
        Assert.Equal(before, await StockStateAsync(s)); Assert.Equal(draft.Snapshot, (await Service().GetAsync(draft.Summary.Id, default)).Snapshot);
        Assert.Equal(auditCount, await db.AuditLogs.CountAsync());
    }
    [ReceivingSqlFact]
    public async Task Server_search_filters_sort_paging_and_detail_transactions_use_saved_documents()
    {
        var s = await ScenarioAsync(); var input = await RequestAsync(s); var draft = await Service().CreateAsync(input, F.ActorId, default);
        var second = await Service().CreateAsync(input with { RequestId = Guid.NewGuid() }, F.ActorId, default);
        var page = await Service().SearchAsync(new(Search: s.Code, Status: "DRAFT", SortBy: "idAsc", PageSize: 1), default);
        Assert.Equal(2, page.TotalCount); Assert.Equal(draft.Summary.Id, page.Items.Single().Id);
        var next = await Service().SearchAsync(new(Search: s.Code, Status: "DRAFT", SortBy: "idAsc", Page: 2, PageSize: 1), default);
        Assert.Equal(second.Summary.Id, next.Items.Single().Id);
        await using var db = F.Context(); var location = await db.Inventories.Where(i => i.InventoryId == s.InventoryIds[0]).Select(i => i.WarehouseLocationId).SingleAsync();
        Assert.Equal(2, (await Service().SearchAsync(new(Search: s.Code, WarehouseLocationId: location, FromDate: Today, ToDate: Today), default)).TotalCount);
        var related = await new ProductBatchQueryRepository(db).GetRelatedInformationAsync(s.BatchId, default);
        Assert.NotNull(related);
        Assert.Equal(2, related.WarehouseTransactions.Count(t => t.TransactionType == "GOODS_ISSUE"));
        Assert.All(related.WarehouseTransactions.Where(t => t.TransactionType == "GOODS_ISSUE"), t => Assert.Equal("DRAFT", t.Status));
        var qc = F.Inspection(s.BatchId, "COMPLETED"); qc.QcResult = "PASS"; qc.QualityGrade = "E"; qc.CompletedAt = DateTime.UtcNow;
        var token = new string('a', 32) + Guid.NewGuid().ToString("N");
        db.Add(qc); db.Add(new QrCode { ProductBatchId = s.BatchId, PublicToken = token, TraceabilityUrl = "http://test.invalid/trace/" + token,
            QrImageUrl = "https://test.invalid/qr.png", IsActive = true, GeneratedAt = DateTime.UtcNow }); await db.SaveChangesAsync();
        var traceBefore = await new TraceabilityRepository(db).GetByPublicTokenAsync(token, default); Assert.Empty(traceBefore!.IssueMilestones);
        await Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default);
        var after = await new ProductBatchQueryRepository(db).GetRelatedInformationAsync(s.BatchId, default);
        Assert.Contains(after.WarehouseTransactions, t => t.TransactionType == "GOODS_ISSUE" && t.Status == "COMMITTED");
        Assert.Single((await new TraceabilityRepository(db).GetByPublicTokenAsync(token, default))!.IssueMilestones);
        using var guest = await F.ApiAsync();
        using var response = await guest.GetAsync("/api/public/traceability/" + token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // E/PASS remains public after stock reaches zero.
    }
    private static string Token(int id, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test", "uc28-test",
        [new Claim("account_id", id.ToString()), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256)));
    [ReceivingSqlFact]
    public async Task HTTP_role_matrix_unknown_fields_validation_and_direct_commit_are_enforced()
    {
        var s = await ScenarioAsync(); var input = await RequestAsync(s); var draft = await Service().CreateAsync(input, F.ActorId, default);
        using var client = await F.ApiAsync();
        var endpoints = new (HttpMethod Method, string Path, object? Body)[] {
            (HttpMethod.Get, "", null), (HttpMethod.Get, "/locations", null), (HttpMethod.Get, "/eligible-orders", null),
            (HttpMethod.Get, $"/eligible-orders/{s.OrderId}", null), (HttpMethod.Get, $"/{draft.Summary.Id}", null),
            (HttpMethod.Post, "", input), (HttpMethod.Put, $"/{draft.Summary.Id}/draft", new UpdateGoodsIssueDraftRequest(Guid.NewGuid(), draft.Snapshot, Today, null, null, input.Lines)),
            (HttpMethod.Post, $"/{draft.Summary.Id}/confirm", Confirm(draft)) };
        foreach (var role in new[] { "SUPPLIER", "DISTRIBUTOR", "QC_STAFF", "WAREHOUSE_MANAGER", "ADMINISTRATOR", "" })
        {
            client.DefaultRequestHeaders.Authorization = role == "" ? null : new AuthenticationHeaderValue("Bearer", Token(F.ActorId, role));
            foreach (var endpoint in endpoints)
            {
                using var request = new HttpRequestMessage(endpoint.Method, "/api/operation/goods-issues" + endpoint.Path);
                if (endpoint.Body is not null) request.Content = JsonContent.Create(endpoint.Body);
                using var response = await client.SendAsync(request);
                Assert.Equal(role == "" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(F.ActorId, "OPERATION_STAFF"));
        using var list = await client.GetAsync("/api/operation/goods-issues"); Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var badPage = await client.GetAsync("/api/operation/goods-issues?pageSize=101"); Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
        var spoof = JsonSerializer.SerializeToNode(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject(); spoof["operationAccountId"] = 999;
        using var bad = await client.PostAsJsonAsync("/api/operation/goods-issues", spoof); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var confirm = await client.PostAsJsonAsync($"/api/operation/goods-issues/{draft.Summary.Id}/confirm", Confirm(draft)); Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        using var edit = await client.PutAsJsonAsync($"/api/operation/goods-issues/{draft.Summary.Id}/draft", new UpdateGoodsIssueDraftRequest(Guid.NewGuid(), draft.Snapshot, Today, null, null, input.Lines)); Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
    }
    private static async Task<Exception?> Try(Func<Task> action) { try { await action(); return null; } catch (Exception e) { return e; } }
    [ReceivingSqlFact]
    public async Task Concurrent_draft_edit_and_confirm_only_one_viewed_version_wins()
    {
        for (var i = 0; i < 2; i++)
        {
            var s = await ScenarioAsync(); var input = await RequestAsync(s); var draft = await Service().CreateAsync(input, F.ActorId, default);
            var edit = new UpdateGoodsIssueDraftRequest(Guid.NewGuid(), draft.Snapshot, Today, "Changed", "Concurrent edit", [new(input.Lines[0].ReservationId, 50)]);
            var actions = i == 0
                ? new[] { Try(() => Service().UpdateDraftAsync(draft.Summary.Id, edit, F.ActorId, default)), Try(() => Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default)) }
                : new[] { Try(() => Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default)), Try(() => Service().UpdateDraftAsync(draft.Summary.Id, edit, F.ActorId, default)) };
            var outcomes = await Task.WhenAll(actions); Assert.Equal(1, outcomes.Count(e => e is null)); Assert.IsType<ConflictException>(outcomes.Single(e => e is not null));
            var saved = await Service().GetAsync(draft.Summary.Id, default); await using var db = F.Context();
            var hand = await db.Inventories.Where(v => v.ProductBatchId == s.BatchId).SumAsync(v => v.QuantityOnHand);
            Assert.Equal(saved.Summary.IssueStatus == "DRAFT" ? 100m : 0m, hand);
            Assert.Equal(saved.Summary.IssueStatus == "DRAFT" ? 50m : 100m, saved.Summary.WeightInKg);
        }
    }
    [ReceivingSqlFact]
    public async Task Fully_issued_first_position_does_not_prevent_picking_second_position()
    {
        var s = await ScenarioAsync(40, [40, 60]); var order = await Service().OrderAsync(s.OrderId, default);
        var first = order.Allocations.Single(a => a.PickedWeightKg == 40);
        var draft = await Service().CreateAsync(new(Guid.NewGuid(), s.OrderId, Today, null, null, [new(first.ReservationId, 40)]), F.ActorId, default);
        await Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default);
        var picking = await Picking().GetAsync(s.OrderId, default); Assert.True(picking.Order.CanPick);
        var next = picking.Lines.Single().Reservations.Single(r => r.Status == "RESERVED");
        await Picking().ConfirmAsync(s.OrderId, new(Guid.NewGuid(), next.Id, s.LineId, next.InventoryId, s.BatchId, 60, 0, null), F.ActorId, default);
        order = await Service().OrderAsync(s.OrderId, default); var remaining = order.Allocations.Single(a => a.CanIssue);
        var finish = await Service().CreateAsync(new(Guid.NewGuid(), s.OrderId, Today, null, null, [new(remaining.ReservationId, 60)]), F.ActorId, default);
        await Service().ConfirmAsync(finish.Summary.Id, Confirm(finish), F.ActorId, default);
        await using var db = F.Context(); Assert.Equal(0, await db.Inventories.Where(v => v.ProductBatchId == s.BatchId).SumAsync(v => v.QuantityOnHand));
    }
    [ReceivingSqlFact]
    public async Task Partial_issue_preserves_other_order_reservation_on_same_inventory()
    {
        var s = await ScenarioAsync(100, [150]);
        await using var db = F.Context(); var order = new PurchaseOrder { DistributorId = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == s.OrderId).Select(o => o.DistributorId).SingleAsync(),
            OrderCode = "LEGACY-" + Guid.NewGuid().ToString("N"), OrderStatus = "RESERVED", DeliveryAddress = "Test", ApprovedByAccountId = F.ActorId, ApprovedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        var line = new OrderDetail { PurchaseOrder = order, CropTypeId = F.CropTypeId, RequestedProductBatchId = s.BatchId,
            RequestedQuantity = 1, ApprovedQuantity = 1, RequestedWeightKg = 20, ApprovedWeightKg = 20, Unit = "Lô" };
        var other = new InventoryReservation { OrderDetail = line, InventoryId = s.InventoryIds[0], ReservedQuantity = 20, ReservationStatus = "RESERVED", ReservedAt = DateTime.UtcNow };
        db.Add(other); await db.SaveChangesAsync();
        var draft = await Service().CreateAsync(await RequestAsync(s, 25), F.ActorId, default);
        await Service().ConfirmAsync(draft.Summary.Id, Confirm(draft), F.ActorId, default);
        var inv = await db.Inventories.AsNoTracking().SingleAsync(v => v.InventoryId == s.InventoryIds[0]);
        Assert.Equal(125, inv.QuantityOnHand); Assert.Equal(95, inv.ReservedQuantity); Assert.Equal(30, inv.AvailableQuantity);
        var saved = await db.InventoryReservations.AsNoTracking().SingleAsync(r => r.InventoryReservationId == other.InventoryReservationId);
        Assert.Equal("RESERVED", saved.ReservationStatus); Assert.Equal(20, saved.ReservedQuantity); Assert.Equal(0, saved.PickedQuantity);
    }
    [ReceivingSqlFact]
    public async Task Missing_workflow_migration_is_reported_without_creating_a_slip()
    {
        var s = await ScenarioAsync(); var request = await RequestAsync(s);
        await using var db = F.Context();
        var definition = await db.Database.SqlQueryRaw<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('TR_INVENTORY_RESERVATION_SYNC')) AS Value").SingleAsync();
        definition = System.Text.RegularExpressions.Regex.Replace(definition, @"\bCREATE(?:\s+OR\s+ALTER)?\s+TRIGGER\b", "CREATE OR ALTER TRIGGER", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        try
        {
            // Only this fixture database is changed; mimic an installation before prerequisite deployment.
            await db.Database.ExecuteSqlRawAsync(definition.Replace("SAW_GOODS_ISSUE_V1", "BEFORE_ISSUE_WORKFLOW"));
            var error = await Assert.ThrowsAsync<ConflictException>(() => Service().CreateAsync(request, F.ActorId, default));
            Assert.Contains("xuất từng phần", error.Message);
            Assert.False(await db.GoodsIssues.AnyAsync(g => g.PurchaseOrderId == s.OrderId));
            Assert.Equal(100, await db.Inventories.Where(i => i.ProductBatchId == s.BatchId).SumAsync(i => i.QuantityOnHand));
        }
        finally { await db.Database.ExecuteSqlRawAsync(definition); }
    }
    [ReceivingSqlFact]
    public async Task Decimal_kg_precision_is_preserved_across_partial_and_final_issue()
    {
        var s = await ScenarioAsync(); var first = await Service().CreateAsync(await RequestAsync(s, 0.001m), F.ActorId, default);
        await Service().ConfirmAsync(first.Summary.Id, Confirm(first), F.ActorId, default);
        await using var db = F.Context();
        var stock = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductBatchId == s.BatchId);
        Assert.Equal(99.999m, stock.QuantityOnHand); Assert.Equal(99.999m, stock.ReservedQuantity);
        var final = await Service().CreateAsync(await RequestAsync(s, 99.999m), F.ActorId, default);
        await Service().ConfirmAsync(final.Summary.Id, Confirm(final), F.ActorId, default);
        stock = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductBatchId == s.BatchId);
        Assert.Equal(0, stock.QuantityOnHand); Assert.Equal(0, stock.ReservedQuantity);
    }
    [ReceivingSqlFact]
    public async Task Multirow_reservation_updates_and_draft_detail_insert_update_delete_are_set_based()
    {
        var s = await ScenarioAsync(100, [40, 60]); var order = await Service().OrderAsync(s.OrderId, default);
        var first = await Service().CreateAsync(new(Guid.NewGuid(), s.OrderId, Today, null, null,
            order.Allocations.Select(r => new GoodsIssueLineRequest(r.ReservationId, 10)).ToList()), F.ActorId, default);
        await Service().ConfirmAsync(first.Summary.Id, Confirm(first), F.ActorId, default);
        var second = await Service().CreateAsync(new(Guid.NewGuid(), s.OrderId, Today, null, null,
            order.Allocations.Select(r => new GoodsIssueLineRequest(r.ReservationId, 5)).ToList()), F.ActorId, default);
        var state = await StockStateAsync(s);
        await using var db = F.Context();
        // A single UPDATE touches both reservations and invokes nested counter synchronization.
        var touched = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE INVENTORY_RESERVATION SET ReservationStatus=ReservationStatus WHERE OrderDetailID={s.LineId}");
        Assert.Equal(2, touched); Assert.Equal(state, await StockStateAsync(s));
        var third = new GoodsIssue { IssueCode = "MULTI-" + Guid.NewGuid().ToString("N"), PurchaseOrderId = s.OrderId,
            OperationAccountId = F.ActorId, IssueStatus = "DRAFT", IssuedAt = Today.ToDateTime(TimeOnly.MinValue) };
        db.Add(third); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO GOODS_ISSUE_DETAIL (GoodsIssueID,OrderDetailID,InventoryReservationID,InventoryID,IssuedQuantity,Unit,WeightInKg) SELECT {third.GoodsIssueId},OrderDetailID,InventoryReservationID,InventoryID,5,N'kg',5 FROM INVENTORY_RESERVATION WHERE OrderDetailID={s.LineId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE GOODS_ISSUE_DETAIL SET WeightInKg=6,IssuedQuantity=6 WHERE GoodsIssueID={third.GoodsIssueId}");
        Assert.Equal(state, await StockStateAsync(s));
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GOODS_ISSUE_DETAIL WHERE GoodsIssueID={third.GoodsIssueId}");
        Assert.Equal(state, await StockStateAsync(s));
        Assert.Equal(80, await db.Inventories.Where(i => i.ProductBatchId == s.BatchId).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(80, await db.Inventories.Where(i => i.ProductBatchId == s.BatchId).SumAsync(i => i.ReservedQuantity));
        Assert.Equal(100, await db.OrderDetails.Where(l => l.OrderDetailId == s.LineId).Select(l => l.PickedWeightKg).SingleAsync());
        var batchCode = await db.ProductBatches.Where(b => b.ProductBatchId == s.BatchId).Select(b => b.BatchCode).SingleAsync();
        var manager = new WarehouseInventoryRepository(db, new OrderReservationAllocator(Options(), Accessor));
        var offer = Assert.Single((await manager.GetSaleOffersAsync(batchCode, 1, 20, default)).Items);
        Assert.Equal(0, offer.AvailableWeightKg); Assert.False(offer.CanPublish);
        var buyerId = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == s.OrderId).Select(o => o.Distributor.AccountId).SingleAsync();
        var distributor = new DistributorOrderRepository(Options(), Accessor);
        var buyerLot = await distributor.LotAsync(buyerId, s.BatchId, default);
        Assert.Equal("PARTIALLY_ISSUED", buyerLot.BatchStatus); Assert.False(buyerLot.CanPurchase);
        Assert.Empty((await distributor.CatalogAsync(buyerId, new(Search: batchCode), default)).Items);
        Assert.Equal("DRAFT", second.Summary.IssueStatus);
    }
    [DistributorBrowserFact]
    public async Task Browser_create_edit_confirm_list_and_stale_form_use_real_API_SQL()
    {
        var main = await ScenarioAsync(); var stale = await ScenarioAsync();
        var draft = await Service().CreateAsync(await RequestAsync(stale), F.ActorId, default);
        using var api = await F.ApiAsync(); var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Capstone_SAW_FE"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node") { WorkingDirectory = Path.Combine(root.FullName, "Capstone_SAW_FE"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("tests/goods-issues-browser.mjs");
        start.Environment["SAW_TEST_API_URL"] = api.BaseAddress!.ToString(); start.Environment["SAW_TEST_ORDER_ID"] = main.OrderId.ToString();
        start.Environment["SAW_TEST_STALE_ISSUE_ID"] = draft.Summary.Id.ToString(); start.Environment["SAW_TEST_TOKEN"] = Token(F.ActorId, "OPERATION_STAFF");
        start.Environment["SAW_TEST_MANAGER_TOKEN"] = Token(F.ActorId, "WAREHOUSE_MANAGER");
        await using (var buyerDb = F.Context())
        {
            var buyerId = await buyerDb.PurchaseOrders.Where(o => o.PurchaseOrderId == main.OrderId).Select(o => o.Distributor.AccountId).SingleAsync();
            start.Environment["SAW_TEST_BUYER_TOKEN"] = Token(buyerId, "DISTRIBUTOR");
        }
        start.Environment["SAW_TEST_EVIDENCE_DIRECTORY"] = Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "SAW_GoodsIssue_Verification_2026-10-16");
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token); } catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        await using var db = F.Context();
        Assert.Equal(0, await db.Inventories.Where(i => i.ProductBatchId == main.BatchId).SumAsync(i => i.QuantityOnHand));
        Assert.Equal(100, await db.Inventories.Where(i => i.ProductBatchId == stale.BatchId).SumAsync(i => i.QuantityOnHand));
    }
}
