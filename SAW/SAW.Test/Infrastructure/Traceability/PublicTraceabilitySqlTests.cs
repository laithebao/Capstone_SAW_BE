using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Traceability.Services;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.Traceability;

public sealed class PublicTraceabilitySqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    private async Task<(ProductBatch Batch, QrCode Qr)> AcceptedAsync()
    {
        var batch = await fixture.BatchAsync();
        await using var db = fixture.Context();
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.BatchStatus, "APPROVED_FOR_STORAGE").SetProperty(b => b.QualityGrade, "A")
            .SetProperty(b => b.VerifiedPackagingType, (string?)null).SetProperty(b => b.VerifiedPackageCount, (int?)null)
            .SetProperty(b => b.VerifiedPackageUnitWeightKg, (decimal?)null));
        var qc = fixture.Inspection(batch.ProductBatchId, "COMPLETED");
        qc.StartedAt = new DateTime(2026, 2, 1); qc.CompletedAt = new DateTime(2026, 2, 2);
        qc.QcResult = "PASS"; qc.QualityGrade = "A"; qc.Note = "PRIVATE_QC_NOTE";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var qr = new QrCode { ProductBatchId = batch.ProductBatchId, PublicToken = token,
            TraceabilityUrl = "https://public.test.invalid/trace/" + token, IsActive = true,
            QrImageUrl = "https://image.test.invalid/qr.png", GeneratedAt = DateTime.UtcNow };
        db.AddRange(qc, qr);
        await db.SaveChangesAsync();
        return (batch, qr);
    }

    [ReceivingSqlFact]
    public async Task AnonymousAndAuthenticatedHttp_ReturnExactlyThePublicWhitelist_AndDoNotWrite()
    {
        var (batch, qr) = await AcceptedAsync();
        await using var db = fixture.Context();
        var before = new { Qr = await db.QrCodes.CountAsync(), Audit = await db.AuditLogs.CountAsync(),
            Batch = await db.ProductBatches.CountAsync(), Inventory = await db.Inventories.CountAsync(),
            Receipt = await db.GoodsReceipts.CountAsync(), Issue = await db.GoodsIssues.CountAsync() };
        using var client = await fixture.ApiAsync();
        var path = "/api/public/traceability/" + qr.PublicToken;
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.True(anonymous.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        var data = body.RootElement.GetProperty("data");
        AssertPublicProperties(data);
        Assert.Equal(batch.BatchCode, data.GetProperty("batchCode").GetString());
        Assert.Equal("QC_COMPLETED", Assert.Single(data.GetProperty("milestones").EnumerateArray()).GetProperty("type").GetString());
        client.DefaultRequestHeaders.Authorization = Bearer("SUPPLIER");
        using var authenticated = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        using var signedIn = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync());
        Assert.Equal(data.GetRawText(), signedIn.RootElement.GetProperty("data").GetRawText());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-or-expired-token");
        using var expired = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, expired.StatusCode);
        // No anonymous privilege was added to management APIs.
        client.DefaultRequestHeaders.Authorization = null;
        using var internalRead = await client.GetAsync($"/api/operation/product-batches/{batch.ProductBatchId}");
        Assert.Equal(HttpStatusCode.Unauthorized, internalRead.StatusCode);
        var after = new { Qr = await db.QrCodes.CountAsync(), Audit = await db.AuditLogs.CountAsync(),
            Batch = await db.ProductBatches.CountAsync(), Inventory = await db.Inventories.CountAsync(),
            Receipt = await db.GoodsReceipts.CountAsync(), Issue = await db.GoodsIssues.CountAsync() };
        Assert.Equal(before, after);
    }

    [ReceivingSqlFact]
    public async Task HttpErrors_Are404ForUnknownToken_And410ForUnavailableBatchOrQr()
    {
        var (batch, qr) = await AcceptedAsync();
        using var client = await fixture.ApiAsync();
        await ErrorAsync(client, "not-a-public-token", HttpStatusCode.NotFound, TraceabilityService.NotFoundMessage);
        await ErrorAsync(client, new string('f', 64), HttpStatusCode.NotFound, TraceabilityService.NotFoundMessage);
        await using var db = fixture.Context();
        foreach (var status in new[] { "RECEIVED", "IN_STOCK", "RESERVED", "PARTIALLY_ISSUED", "ISSUED" })
        {
            await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.BatchStatus, status));
            using var success = await client.GetAsync("/api/public/traceability/" + qr.PublicToken);
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }
        foreach (var status in new[] { "REJECTED", "QUARANTINE", "CANCELLED", "SUBMITTED", "PENDING_QC" })
        {
            await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.BatchStatus, status));
            await ErrorAsync(client, qr.PublicToken, HttpStatusCode.Gone, TraceabilityService.UnavailableMessage);
        }
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.BatchStatus, "IN_STOCK"));
        await db.QrCodes.Where(q => q.QrCodeId == qr.QrCodeId).ExecuteUpdateAsync(s => s.SetProperty(q => q.IsActive, false));
        await ErrorAsync(client, qr.PublicToken, HttpStatusCode.Gone, TraceabilityService.UnavailableMessage);
    }

    [ReceivingSqlFact]
    public async Task NewerDraftOrFailure_IsNotBypassedByAnOlderPass()
    {
        var (batch, qr) = await AcceptedAsync();
        await using var db = fixture.Context();
        var laterDraft = fixture.Inspection(batch.ProductBatchId);
        db.QcInspections.Add(laterDraft);
        await db.SaveChangesAsync();
        var service = new TraceabilityService(new TraceabilityRepository(db));
        await Assert.ThrowsAsync<GoneException>(() => service.GetAsync(qr.PublicToken, default));
        // A further completed FAIL must remain blocked even if batch grade/status are stale.
        var fail = fixture.Inspection(batch.ProductBatchId, "COMPLETED");
        fail.StartedAt = laterDraft.StartedAt.AddSeconds(1); fail.CompletedAt = fail.StartedAt.AddSeconds(1);
        fail.QcResult = "FAIL"; fail.QualityGrade = "E";
        db.QcInspections.Add(fail);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<GoneException>(() => service.GetAsync(qr.PublicToken, default));
    }

    [ReceivingSqlFact]
    public async Task MilestonesUseCommittedTransactions_AndWarehouseDataIsNotPublic()
    {
        var (batch, qr) = await AcceptedAsync();
        await using var db = fixture.Context();
        var old = new WarehouseLocation { LocationCode = "OLD", ZoneName = "Historical zone", RackName = "PRIVATE_RACK", BinName = "PRIVATE_BIN" };
        var current = new WarehouseLocation { LocationCode = "CURRENT", ZoneName = "Current zone", RackName = "PRIVATE_RACK_2", BinName = "PRIVATE_BIN_2" };
        db.WarehouseLocations.AddRange(old, current);
        await db.SaveChangesAsync();
        var receiptAt = new DateTime(2026, 3, 1);
        db.GoodsReceipts.AddRange(
            new GoodsReceipt { ReceiptCode = "COMMITTED-RECEIPT", ProductBatchId = batch.ProductBatchId, WarehouseLocationId = old.WarehouseLocationId,
                OperationAccountId = fixture.ActorId, ReceivedQuantity = 10, WeightInKg = 100, Unit = "Box", ReceiptStatus = "COMMITTED", ReceivedAt = receiptAt.AddDays(-1), CommittedAt = receiptAt, Note = "PRIVATE_RECEIPT_NOTE" },
            new GoodsReceipt { ReceiptCode = "DRAFT-RECEIPT", ProductBatchId = batch.ProductBatchId, WarehouseLocationId = current.WarehouseLocationId,
                OperationAccountId = fixture.ActorId, ReceivedQuantity = 10, WeightInKg = 100, Unit = "Box", ReceiptStatus = "DRAFT", ReceivedAt = receiptAt.AddDays(10), CommittedAt = receiptAt.AddDays(10) });
        await db.SaveChangesAsync();
        // The isolated fixture lacks the production computed column definition; seed its
        // generated AvailableQuantity value explicitly, without changing production schema.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO INVENTORY(ProductBatchID,WarehouseLocationID,QuantityOnHand,ReservedQuantity,AvailableQuantity,Unit,LastUpdatedAt) VALUES({batch.ProductBatchId},{old.WarehouseLocationId},0,0,0,'Box',{receiptAt}),({batch.ProductBatchId},{current.WarehouseLocationId},5,0,5,'Box',{receiptAt})");
        var inventory = await db.Inventories.SingleAsync(i => i.ProductBatchId == batch.ProductBatchId && i.WarehouseLocationId == current.WarehouseLocationId);
        var distributor = new Distributor { AccountId = fixture.ActorId, DistributorCode = "TRACE-DIST", DistributorName = "PRIVATE_CUSTOMER", TaxCode = "PRIVATE_TAX", Address = "PRIVATE_ADDRESS" };
        var order = new PurchaseOrder { Distributor = distributor, OrderCode = "TRACE-ORDER", DeliveryAddress = "PRIVATE_DELIVERY_ADDRESS", ExpectedDeliveryDate = new DateOnly(2026, 3, 5) };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ORDER_DETAIL(PurchaseOrderID,CropTypeID,RequestedQuantity,ApprovedQuantity,Unit,RequestedWeightKg,ApprovedWeightKg,ReservedWeightKg,PickedWeightKg,UnitPrice,TaxAmount,LineSubtotal,LineTotal) VALUES({order.PurchaseOrderId},{fixture.CropTypeId},5,5,'Box',50,50,50,50,0,0,0,0)");
        var detail = await db.OrderDetails.SingleAsync(d => d.PurchaseOrderId == order.PurchaseOrderId);
        var reservation = new InventoryReservation { InventoryId = inventory.InventoryId, OrderDetailId = detail.OrderDetailId, ReservedQuantity = 5 };
        db.InventoryReservations.Add(reservation);
        await db.SaveChangesAsync();
        var issueAt = receiptAt.AddDays(1);
        foreach (var committed in new[] { false, true })
        {
            var issue = new GoodsIssue { IssueCode = committed ? "COMMITTED-ISSUE" : "DRAFT-ISSUE", PurchaseOrderId = order.PurchaseOrderId,
                OperationAccountId = fixture.ActorId, ReceiverName = "PRIVATE_RECEIVER", Note = "PRIVATE_ISSUE_NOTE",
                IssueStatus = committed ? "COMMITTED" : "DRAFT", IssuedAt = issueAt.AddDays(-1), CommittedAt = committed ? issueAt : issueAt.AddDays(10) };
            // Two details from the same committed issue must yield only one public milestone.
            for (var i = 0; i < 2; i++) issue.GoodsIssueDetails.Add(new GoodsIssueDetail
            { InventoryId = inventory.InventoryId, OrderDetailId = detail.OrderDetailId, InventoryReservationId = reservation.InventoryReservationId,
                IssuedQuantity = 1, WeightInKg = 10, Unit = "Box" });
            db.GoodsIssues.Add(issue);
        }
        await db.SaveChangesAsync();
        var result = await new TraceabilityService(new TraceabilityRepository(db)).GetAsync(qr.PublicToken, default);
        Assert.Equal("PASS", result.Quality.Result);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(receiptAt, DateTimeKind.Utc)), Assert.Single(result.Milestones, m => m.Type == "RECEIVED").OccurredAt);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(issueAt, DateTimeKind.Utc)), Assert.Single(result.Milestones, m => m.Type == "ISSUED").OccurredAt);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AssertPublicProperties(json);
        Assert.DoesNotContain("PRIVATE_", json.GetRawText());
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.BatchStatus, "ISSUED"));
        result = await new TraceabilityService(new TraceabilityRepository(db)).GetAsync(qr.PublicToken, default);
        Assert.Equal("PASS", result.Quality.Result);
    }

    [ReceivingSqlFact]
    public async Task InspectionUsesAttachedVersion_AndProjectsTypedEvidenceWithoutNotes()
    {
        var (batch, qr) = await AcceptedAsync();
        await using var db = fixture.Context();
        var qc = await db.QcInspections.SingleAsync(i => i.ProductBatchId == batch.ProductBatchId);
        qc.QualityGrade = "B"; qc.SamplingRatio = .1m; qc.SampleSize = 10;
        var entity = await db.ProductBatches.FindAsync(batch.ProductBatchId);
        entity!.QualityGrade = "B";
        var attached = await db.InspectionStandardVersions.FindAsync(qc.InspectionStandardVersionId);
        var newer = new InspectionStandardVersion { InspectionStandardSetId = attached!.InspectionStandardSetId, VersionNo = 99, VersionStatus = "PUBLISHED" };
        var number = new InspectionCriterion { InspectionStandardVersionId = attached.InspectionStandardVersionId,
            CriterionCode = "TRACE-N", CriterionName = "Defect ratio", CriterionGroup = "SENSORY", DataType = "NUMBER", Unit = "%", IsRequired = true,
            GradeRules = [new() { Grade = "A", MinValue = 0, MaxValue = 1 }, new() { Grade = "B", MinValue = 2, MaxValue = 3 }] };
        var optional = new InspectionCriterion { InspectionStandardVersionId = attached.InspectionStandardVersionId,
            CriterionCode = "TRACE-OPTIONAL", CriterionName = "Optional result", CriterionGroup = "LAB", DataType = "BOOLEAN" };
        db.AddRange(newer, number, optional);
        qc.ResultDetails.Add(new InspectionResultDetail { InspectionCriterion = number, NumericValue = 1.5m, EvaluatedGrade = "B", IsPassed = true, Remarks = "PRIVATE_REMARKS" });
        qc.ResultDetails.Add(new InspectionResultDetail { InspectionCriterion = optional, BooleanValue = null, IsPassed = true });
        await db.SaveChangesAsync();
        var service = new TraceabilityService(new TraceabilityRepository(db));
        var beforeAudit = await db.AuditLogs.CountAsync();
        var result = await service.GetAsync(qr.PublicToken, default);
        Assert.Equal(attached.VersionNo, result.Quality.Standard!.Version);
        Assert.NotEqual(newer.VersionNo, result.Quality.Standard.Version);
        Assert.Equal("B", result.Quality.Grade);
        var measured = Assert.Single(result.Quality.Criteria, c => c.Code == "TRACE-N");
        Assert.Equal(1.5m, measured.NumericValue); Assert.Equal("%", measured.Unit);
        Assert.Contains("không nằm trong khoảng nào", measured.AssessmentBasis);
        var absent = Assert.Single(result.Quality.Criteria, c => c.Code == "TRACE-OPTIONAL");
        Assert.False(absent.HasResult); Assert.Null(absent.IsPassed);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AssertPublicProperties(json); Assert.DoesNotContain("PRIVATE_", json.GetRawText());
        Assert.Equal(beforeAudit, await db.AuditLogs.CountAsync());
        Assert.Equal("PRIVATE_REMARKS", await db.InspectionResultDetails.Where(d => d.QcInspectionId == qc.QcInspectionId && d.InspectionCriterionId == number.InspectionCriterionId).Select(d => d.Remarks).SingleAsync());
    }

    private static void AssertPublicProperties(JsonElement data)
    {
        static void Keys(JsonElement value, params string[] expected) =>
            Assert.Equal(expected.OrderBy(x => x), value.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        Keys(data, "batchCode", "productName", "cropTypeName", "supplierName", "origin", "harvestDate", "expiryDate", "quality", "milestones");
        Keys(data.GetProperty("origin"), "areaName", "region", "province");
        Keys(data.GetProperty("quality"), "grade", "result", "startedAt", "completedAt", "standard", "sampling", "criteria", "gradeExplanation", "determiningCriteria");
        var quality = data.GetProperty("quality");
        Keys(quality.GetProperty("standard"), "code", "name", "version");
        if (quality.GetProperty("sampling").ValueKind != JsonValueKind.Null) Keys(quality.GetProperty("sampling"), "ratioPercent", "sampleWeightKg");
        foreach (var c in quality.GetProperty("criteria").EnumerateArray())
        {
            Keys(c, "code", "name", "groupLabel", "dataType", "unit", "numericValue", "textValue", "booleanValue", "hasResult", "evaluatedGrade", "isPassed", "assessmentBasis", "rules");
            foreach (var r in c.GetProperty("rules").EnumerateArray()) Keys(r, "grade", "minValue", "maxValue", "requiredTextValue", "isFailRule");
        }
        foreach (var milestone in data.GetProperty("milestones").EnumerateArray()) Keys(milestone, "type", "label", "occurredAt");
    }

    private static async Task ErrorAsync(HttpClient client, string token, HttpStatusCode status, string message)
    {
        using var response = await client.GetAsync("/api/public/traceability/" + token);
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(message, body.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("data").ValueKind);
    }

    private AuthenticationHeaderValue Bearer(string role)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = "uc28-test", aud = "uc28-test",
            exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), account_id = fixture.ActorId.ToString(), role }));
        var signature = Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey), Encoding.UTF8.GetBytes(header + "." + payload)));
        return new("Bearer", header + "." + payload + "." + signature);
    }
}
