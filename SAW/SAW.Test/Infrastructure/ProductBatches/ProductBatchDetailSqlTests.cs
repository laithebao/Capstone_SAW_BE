using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.GoodsReceipts;

namespace SAW.Test.Infrastructure.ProductBatches;

// Existing fixture creates/drops its own GUID DB; QC records are saved test data, not QC-engine changes.
public sealed class ProductBatchDetailSqlTests(GoodsReceiptSqlFixture fixture) : IClassFixture<GoodsReceiptSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Database;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Token(string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "uc28-test", "uc28-test", [new Claim("account_id", F.ActorId.ToString()), new Claim(ClaimTypes.Role, role)],
        expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256)));

    private async Task<(ProductBatch Batch, GoodsReceipt Receipt)> SeedAsync(string prefix)
    {
        var batch = await F.BatchAsync();
        var stamp = new DateTime(2026, 10, 14, 0, 0, 0);
        await using var db = F.Context();
        var qc = F.Inspection(batch.ProductBatchId, "COMPLETED");
        qc.QcResult = "PASS"; qc.QualityGrade = "E"; qc.StartedAt = stamp; qc.CompletedAt = stamp.AddMinutes(1);
        qc.Note = prefix + " QC saved note";
        var from = new WarehouseLocation { LocationCode = prefix + "-A", ZoneName = "UC29 fixture", LocationStatus = "ACTIVE" };
        var to = new WarehouseLocation { LocationCode = prefix + "-B", ZoneName = "UC29 fixture", LocationStatus = "ACTIVE" };
        db.AddRange(qc, from, to); await db.SaveChangesAsync();
        var inventory = new Inventory { ProductBatchId = batch.ProductBatchId, WarehouseLocationId = from.WarehouseLocationId,
            Unit = "kg", QuantityOnHand = 100, ReservedQuantity = 0, LastUpdatedAt = stamp };
        var receipt = new GoodsReceipt { ProductBatchId = batch.ProductBatchId, WarehouseLocationId = from.WarehouseLocationId,
            ReceiptCode = prefix + "-GR", OperationAccountId = F.ActorId, ReceivedQuantity = 10, Unit = "Box",
            WeightInKg = 100, ReceivedAt = stamp, ReceiptStatus = "DRAFT", Note = prefix + " receipt saved note" };
        db.AddRange(inventory, receipt);
        db.StockTransfers.Add(new StockTransfer { ProductBatchId = batch.ProductBatchId,
            FromWarehouseLocationId = from.WarehouseLocationId, ToWarehouseLocationId = to.WarehouseLocationId,
            TransferCode = prefix + "-ST", OperationAccountId = F.ActorId, Quantity = 5, Unit = "kg",
            SourceTrackingId = prefix + "-source", DestinationTrackingId = prefix + "-dest", TransferStatus = "COMPLETED",
            TransferredAt = stamp, Note = prefix + " transfer saved note" });
        db.BatchStatusHistories.Add(new BatchStatusHistory { ProductBatchId = batch.ProductBatchId,
            OldStatus = "SUBMITTED", NewStatus = "PENDING_QC", ChangedByAccountId = F.ActorId,
            ChangedAt = stamp, ChangeReason = prefix + " saved status reason" });
        db.BatchStatusHistories.Add(new BatchStatusHistory { ProductBatchId = batch.ProductBatchId,
            OldStatus = "PENDING_QC", NewStatus = "APPROVED_FOR_STORAGE", ChangedByAccountId = null,
            ChangedAt = stamp, ChangeReason = null });
        await db.SaveChangesAsync();
        // Test schema generated from EF treats these computed columns as ordinary stored values.
        // Seed the recorded values explicitly in this disposable fixture only.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO STOCK_ADJUSTMENT(AdjustmentCode,InventoryID,CreatedByAccountID,SystemQuantity,ActualQuantity,
                DifferenceQuantity,VariancePercent,AdjustmentType,Reason,AdjustmentStatus,CreatedAt)
            VALUES({prefix + "-ADJ"},{inventory.InventoryId},{F.ActorId},100,102,2,2,N'INCREASE',
                {prefix + " adjustment saved reason"},N'APPROVED',{stamp});
            """);
        var distributor = await db.Distributors.FirstOrDefaultAsync(d => d.AccountId == F.ActorId);
        if (distributor is null)
        {
            distributor = new Distributor { AccountId = F.ActorId, DistributorCode = prefix, DistributorName = "UC29 fixture",
                TaxCode = prefix, Address = "Fixture" };
            db.Distributors.Add(distributor); await db.SaveChangesAsync();
        }
        var order = new PurchaseOrder { DistributorId = distributor.DistributorId, OrderCode = prefix + "-PO",
            DeliveryAddress = "Fixture", CreatedAt = stamp };
        db.PurchaseOrders.Add(order); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ORDER_DETAIL(PurchaseOrderID,CropTypeID,RequestedProductBatchID,RequestedQuantity,ApprovedQuantity,
                Unit,RequestedWeightKg,ApprovedWeightKg,ReservedWeightKg,PickedWeightKg,UnitPrice,TaxAmount,LineSubtotal,LineTotal)
            VALUES({order.PurchaseOrderId},{F.CropTypeId},{batch.ProductBatchId},3,3,N'kg',3,3,3,3,1,0,3,3);
            """);
        var orderDetail = await db.OrderDetails.SingleAsync(d => d.PurchaseOrderId == order.PurchaseOrderId);
        var reservation = new InventoryReservation { OrderDetailId = orderDetail.OrderDetailId, InventoryId = inventory.InventoryId,
            ReservedQuantity = 3, PickedQuantity = 3, ReservedAt = stamp };
        var issue = new GoodsIssue { PurchaseOrderId = order.PurchaseOrderId, OperationAccountId = F.ActorId,
            IssueCode = prefix + "-GI", IssueStatus = "COMMITTED", IssuedAt = stamp, CommittedAt = stamp,
            Note = prefix + " issue saved note" };
        db.AddRange(reservation, issue); await db.SaveChangesAsync();
        db.GoodsIssueDetails.Add(new GoodsIssueDetail { GoodsIssueId = issue.GoodsIssueId, OrderDetailId = orderDetail.OrderDetailId,
            InventoryReservationId = reservation.InventoryReservationId, InventoryId = inventory.InventoryId,
            IssuedQuantity = 3, Unit = "kg", WeightInKg = 3 });
        await db.SaveChangesAsync();
        return (batch, receipt);
    }

    [ReceivingSqlFact]
    public async Task Related_query_maps_all_saved_sources_filters_batch_and_has_stable_order()
    {
        var prefix = "UC29-" + Guid.NewGuid().ToString("N")[..8];
        var (batch, receipt) = await SeedAsync(prefix);
        await SeedAsync("OTHER-" + Guid.NewGuid().ToString("N")[..8]);
        await using var db = F.Context();
        var repository = new ProductBatchQueryRepository(db);
        var data = await repository.GetRelatedInformationAsync(batch.ProductBatchId, default);
        Assert.Equal("PASS", data.LatestQcInspection!.QcResult); Assert.Equal("E", data.LatestQcInspection.QualityGrade);
        Assert.StartsWith(prefix, data.LatestQcInspection.Note);
        Assert.Equal("UC28 Test", data.LatestQcInspection.QcStaffName);
        Assert.Equal(receipt.GoodsReceiptId, Assert.Single(data.GoodsReceipts).Id);
        Assert.Contains(prefix + "-A", data.GoodsReceipts[0].LocationName);
        Assert.Equal(4, data.WarehouseTransactions.Count);
        Assert.Equal(new[] { "GOODS_ISSUE", "GOODS_RECEIPT", "STOCK_ADJUSTMENT", "STOCK_TRANSFER" },
            data.WarehouseTransactions.Select(t => t.TransactionType));
        Assert.All(data.WarehouseTransactions, t => Assert.StartsWith(prefix, t.Code));
        Assert.Equal(2, data.WarehouseTransactions.Single(t => t.TransactionType == "STOCK_ADJUSTMENT").Quantity);
        Assert.Equal(100, data.WarehouseTransactions.Single(t => t.TransactionType == "GOODS_RECEIPT").WeightInKg);
        Assert.Contains(data.StatusHistory, h => h.ChangeReason == prefix + " saved status reason" && h.ChangedByName == "UC28 Test");
        Assert.Contains(data.StatusHistory, h => h.ChangedByName == null && h.ChangeReason == null);
        Assert.Equal(data.StatusHistory.OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id).Select(h => h.Id),
            data.StatusHistory.Select(h => h.Id));
        Assert.Equal(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId)
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.BatchStatusHistoryId)
            .Select(h => h.BatchStatusHistoryId).ToListAsync(), data.StatusHistory.Select(h => h.Id));
        Assert.Equal(JsonSerializer.Serialize(data, Json), JsonSerializer.Serialize(await repository.GetRelatedInformationAsync(batch.ProductBatchId, default), Json));
        Assert.Empty(db.ChangeTracker.Entries());
        var empty = await repository.GetRelatedInformationAsync(long.MaxValue, default);
        Assert.Null(empty.LatestQcInspection); Assert.Empty(empty.GoodsReceipts);
        Assert.Empty(empty.StatusHistory); Assert.Empty(empty.WarehouseTransactions);
    }

    [ReceivingSqlFact]
    public async Task Latest_qc_uses_started_at_then_id_and_does_not_fall_back_to_older_pass()
    {
        foreach (var status in new[] { "DRAFT", "IN_PROGRESS", "COMPLETED" })
        {
            var batch = await F.BatchAsync();
            await using var db = F.Context();
            var older = F.Inspection(batch.ProductBatchId, "COMPLETED");
            older.StartedAt = new DateTime(2026, 1, 1); older.CompletedAt = older.StartedAt.AddMinutes(1);
            older.QcResult = "PASS"; older.QualityGrade = "E";
            db.QcInspections.Add(older); await db.SaveChangesAsync();
            var latest = F.Inspection(batch.ProductBatchId, status);
            latest.StartedAt = older.StartedAt; // tied timestamp: ID must decide
            if (status == "COMPLETED") { latest.QcResult = "FAIL"; latest.QualityGrade = "B"; latest.CompletedAt = latest.StartedAt.AddMinutes(2); }
            db.QcInspections.Add(latest); await db.SaveChangesAsync();
            var insertedLaterButOlder = F.Inspection(batch.ProductBatchId, "COMPLETED");
            insertedLaterButOlder.StartedAt = older.StartedAt.AddDays(-1);
            insertedLaterButOlder.QcResult = "PASS"; insertedLaterButOlder.QualityGrade = "A";
            db.QcInspections.Add(insertedLaterButOlder); await db.SaveChangesAsync();
            var data = await new ProductBatchQueryRepository(db).GetRelatedInformationAsync(batch.ProductBatchId, default);
            Assert.Equal(latest.QcInspectionId, data.LatestQcInspection!.Id);
            Assert.Equal(status, data.LatestQcInspection.InspectionStatus);
            Assert.Equal(latest.QcResult, data.LatestQcInspection.QcResult);
            Assert.Equal(latest.QualityGrade, data.LatestQcInspection.QualityGrade);
        }
    }

    private async Task<string> SnapshotAsync(long batchId)
    {
        await using var db = F.Context();
        return JsonSerializer.Serialize(new
        {
            Batch = await db.ProductBatches.AsNoTracking().Where(b => b.ProductBatchId == batchId)
                .Select(b => new { b.BatchStatus, b.QualityGrade, b.CreatedAt, b.UpdatedAt, b.VerifiedQuantity, b.VerifiedWeightInKg,
                    b.VerifiedPackagingType, b.VerifiedPackageCount, b.VerifiedPackageUnitWeightKg, b.ReceivingNote }).SingleAsync(),
            Related = await new ProductBatchQueryRepository(db).GetRelatedInformationAsync(batchId, default),
            Inventories = await db.Inventories.AsNoTracking().Where(i => i.ProductBatchId == batchId).OrderBy(i => i.InventoryId)
                .Select(i => new { i.InventoryId, i.QuantityOnHand, i.ReservedQuantity, i.LastUpdatedAt }).ToListAsync(),
            QcCount = await db.QcInspections.CountAsync(), ReceiptCount = await db.GoodsReceipts.CountAsync(),
            HistoryCount = await db.BatchStatusHistories.CountAsync(), AuditCount = await db.AuditLogs.CountAsync(),
            QrCount = await db.QrCodes.CountAsync()
        }, Json);
    }

    [ReceivingSqlFact]
    public async Task Http_detail_all_four_read_roles_get_sections_and_get_does_not_write()
    {
        var (batch, _) = await SeedAsync("HTTP-" + Guid.NewGuid().ToString("N")[..8]);
        using var client = await F.ApiAsync();
        var before = await SnapshotAsync(batch.ProductBatchId);
        foreach (var role in new[] { "OPERATION_STAFF", "QC_STAFF", "WAREHOUSE_MANAGER", "ADMINISTRATOR" })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role));
            using var response = await client.GetAsync($"/api/operation/product-batches/{batch.ProductBatchId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var detail = json.GetProperty("data").Deserialize<ProductBatchDetail>(Json)!;
            Assert.Equal("PASS", detail.LatestQcInspection!.QcResult); Assert.Equal("E", detail.LatestQcInspection.QualityGrade);
            Assert.Single(detail.GoodsReceipts); Assert.NotEmpty(detail.StatusHistory); Assert.Equal(4, detail.WarehouseTransactions.Count);
            Assert.False(detail.CanUpdateReceivingInformation);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("SUPPLIER"));
        using var forbidden = await client.GetAsync($"/api/operation/product-batches/{batch.ProductBatchId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(before, await SnapshotAsync(batch.ProductBatchId));
    }

    [GoodsReceiptBrowserFact]
    public async Task Browser_detail_shows_saved_sections_receipt_route_empty_and_mobile_without_writes()
    {
        var (batch, receipt) = await SeedAsync("WEB-" + Guid.NewGuid().ToString("N")[..8]);
        var empty = await F.BatchAsync();
        var pending = await F.BatchAsync();
        await using (var seed = F.Context())
        {
            var older = F.Inspection(pending.ProductBatchId, "COMPLETED");
            older.StartedAt = new DateTime(2026, 10, 1); older.CompletedAt = older.StartedAt.AddMinutes(1);
            older.QcResult = "PASS"; older.QualityGrade = "A";
            var latest = F.Inspection(pending.ProductBatchId, "DRAFT"); latest.StartedAt = older.StartedAt.AddDays(1);
            seed.AddRange(older, latest); await seed.SaveChangesAsync();
        }
        using var client = await F.ApiAsync();
        var before = await SnapshotAsync(batch.ProductBatchId);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Capstone_SAW_FE"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node") { WorkingDirectory = Path.Combine(root.FullName, "Capstone_SAW_FE"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("tests/product-batch-detail-browser.mjs");
        start.Environment["SAW_TEST_API_URL"] = client.BaseAddress!.ToString();
        start.Environment["SAW_TEST_BATCH_ID"] = batch.ProductBatchId.ToString();
        start.Environment["SAW_TEST_EMPTY_BATCH_ID"] = empty.ProductBatchId.ToString();
        start.Environment["SAW_TEST_PENDING_BATCH_ID"] = pending.ProductBatchId.ToString();
        start.Environment["SAW_TEST_RECEIPT_ID"] = receipt.GoodsReceiptId.ToString();
        start.Environment["SAW_TEST_TOKEN"] = Token("OPERATION_STAFF");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var result = await output + await error;
        Assert.True(process.ExitCode == 0, result);
        Assert.Equal(before, await SnapshotAsync(batch.ProductBatchId));
    }
}
