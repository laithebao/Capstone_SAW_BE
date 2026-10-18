using System.Diagnostics;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Test.Infrastructure.DistributorOrders;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [DistributorBrowserFact]
    public async Task Browser_staff_manager_customer_disposal_decline_and_accept_use_real_API_and_SQL()
    {
        var manager = await Manager();
        var cases = new List<object>(); var orders = new Dictionary<string, (long Order, long Batch)>();
        foreach (var flow in new[] { "disposal", "decline", "accept" })
        {
            var s = await Scenario(); var sale = await SalesOrder(s.Batch);
            await using var db = F.Context(); var code = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == sale.Order).Select(o => o.OrderCode).SingleAsync();
            var batchCode = await db.ProductBatches.Where(b => b.ProductBatchId == s.Batch).Select(b => b.BatchCode).SingleAsync();
            cases.Add(new { flow, kind = flow == "disposal" ? "DISPOSAL" : "COUNT", batch = s.Batch, batchCode, order = sale.Order, orderCode = code, customerToken = Token(sale.Customer, "DISTRIBUTOR") });
            orders.Add(flow, (sale.Order, s.Batch));
        }
        using var api = await F.ApiAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Capstone_SAW_FE"))) root = root.Parent;
        Assert.NotNull(root);
        var evidence = Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY") ?? Path.Combine(root.FullName, "Capstone_SAW_BE", "test-results", "stock-adjustment-order-resolution", "browser");
        Directory.CreateDirectory(evidence);
        var start = new ProcessStartInfo("node") { WorkingDirectory = Path.Combine(root.FullName, "Capstone_SAW_FE"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("tests/stock-adjustment-order-resolution-browser.mjs");
        start.Environment["SAW_TEST_API_URL"] = api.BaseAddress!.ToString(); start.Environment["SAW_TEST_TOKEN"] = Token(F.ActorId, "OPERATION_STAFF");
        start.Environment["SAW_TEST_MANAGER_TOKEN"] = Token(manager, "WAREHOUSE_MANAGER");
        start.Environment["SAW_TEST_RESOLUTION_CASES"] = JsonSerializer.Serialize(cases); start.Environment["SAW_TEST_EVIDENCE_DIRECTORY"] = evidence;
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(8)).Token); } catch { process.Kill(true); throw; }
        var log = await output + await error; await File.WriteAllTextAsync(Path.Combine(evidence, "stock-adjustment-order-resolution-browser.log"), log);
        Assert.True(process.ExitCode == 0, log);
        await using var check = F.Context();
        foreach (var (flow, tuple) in orders)
        {
            var a = await check.StockAdjustments.SingleAsync(a => a.Inventory.ProductBatchId == tuple.Batch);
            Assert.Equal("APPROVED", a.AdjustmentStatus);
            Assert.Equal(flow == "disposal" ? 0 : 80, await check.Inventories.Where(i => i.ProductBatchId == tuple.Batch).SumAsync(i => i.QuantityOnHand));
            Assert.Equal(flow == "accept" ? "PICKED" : "CANCELLED", await check.PurchaseOrders.Where(o => o.PurchaseOrderId == tuple.Order).Select(o => o.OrderStatus).SingleAsync());
        }
    }

    [ReceivingSqlFact]
    public async Task Resolution_API_enforces_roles_unknown_fields_ownership_and_persistent_inbox_ownership()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default); var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        using var api = await F.ApiAsync();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(F.ActorId, "OPERATION_STAFF"));
        var forbidden = await api.PostAsJsonAsync($"api/warehouse-manager/stock-adjustments/{a.Summary.Id}/proposal", new { requestId = Guid.NewGuid(), expectedVersion = sent.Version, action = "CANCEL", reason = "Staff must not cancel" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(manager, "WAREHOUSE_MANAGER"));
        var removedAction = await api.PostAsJsonAsync($"api/warehouse-manager/stock-adjustments/{a.Summary.Id}/proposal", new { requestId = Guid.NewGuid(), expectedVersion = sent.Version, action = "CANCEL", reason = "Removed manager action" });
        Assert.Equal(HttpStatusCode.BadRequest, removedAction.StatusCode);
        Assert.Equal("RESERVED", (await Service().GetAsync(a.Summary.Id, default)).Resolution!.Order!.Status);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(sale.Customer, "DISTRIBUTOR"));
        var tampered = await api.PostAsJsonAsync($"api/distributor/orders/{sale.Order}/adjustments/respond", new { requestId = Guid.NewGuid(), expectedVersion = sent.Version, proposalId = p.Id, revision = p.Revision, decision = "ACCEPT", reason = (string?)null, actorId = manager, total = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"api/distributor/orders/{sale.Order}/adjustments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("api/notifications")).StatusCode);
        await using var db = F.Context(); var managerNotice = await db.Notifications.Where(n => n.AccountId == manager).Select(n => n.NotificationId).FirstAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync($"api/notifications/{managerNotice}/read", null)).StatusCode);
        api.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync($"api/distributor/orders/{sale.Order}/adjustments")).StatusCode);
    }
}
