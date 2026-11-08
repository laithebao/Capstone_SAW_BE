using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Services;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.ProductBatches;

public sealed class ReceivingQcLockTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    private static readonly VerifiedReceivingDetails Changes = new(12m, 125m, "New box", 12, 10m, "Changed");
    private ProductBatchVerificationRepository Repository(AppDbContext db) => new(db, new HttpContextAccessor());
    private Task<ProductBatchReceivingUpdateResult> Update(AppDbContext db, ProductBatch batch) =>
        Repository(db).UpdateAsync(batch.ProductBatchId, batch.UpdatedAt, batch.CreatedAt,
            fixture.ActorId, Changes, CancellationToken.None);

    private async Task<string> Snapshot(long id)
    {
        await using var db = fixture.Context();
        var b = await db.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == id);
        return JsonSerializer.Serialize(new { b.VerifiedQuantity, b.VerifiedWeightInKg, b.VerifiedPackagingType,
            b.VerifiedPackageCount, b.VerifiedPackageUnitWeightKg, b.ReceivingNote, b.UpdatedAt,
            b.BatchCode, b.SupplierId, b.CropTypeId, b.ProductName, b.GrowingAreaId, b.HarvestDate,
            b.DeclaredQuantity, b.WeightInKg, b.Unit, b.PackagingType, b.PackageCount, b.PackageUnitWeightKg,
            b.Note, b.ExpectedMinTempC, b.ExpectedMaxTempC, b.ExpectedMinHumidityPct, b.ExpectedMaxHumidityPct,
            b.ExpectedDeliveryDate, b.ExpiryDate, b.ShelfLifeDaysSnapshot, b.BatchStatus });
    }

    private async Task<int> UpdateAuditCount(long id)
    {
        await using var db = fixture.Context();
        return await db.AuditLogs.CountAsync(a => a.EntityName == "PRODUCT_BATCH" &&
            a.EntityId == $"ProductBatchId={id}" && a.Description == "Receiving details corrected.");
    }

    [ReceivingSqlFact]
    public async Task DraftInProgressAndCompleted_AllBlockWithoutWrites()
    {
        foreach (var status in new[] { "DRAFT", "IN_PROGRESS", "COMPLETED" })
        {
            var batch = await fixture.BatchAsync();
            await using (var qc = fixture.Context())
            {
                qc.QcInspections.Add(fixture.Inspection(batch.ProductBatchId, status));
                await qc.SaveChangesAsync();
            }
            var before = await Snapshot(batch.ProductBatchId);
            await using var db = fixture.Context();
            Assert.Equal(ProductBatchReceivingUpdateResult.QcReceived, await Update(db, batch));
            Assert.Equal(before, await Snapshot(batch.ProductBatchId));
            Assert.Equal(0, await UpdateAuditCount(batch.ProductBatchId));
            var service = new ProductBatchService(new ProductBatchQueryRepository(db), Repository(db));
            var detail = await service.GetAsync(batch.ProductBatchId, CancellationToken.None);
            Assert.False(detail.CanUpdateReceivingInformation);
            Assert.Equal(ProductBatchService.QcReceivingLockMessage, detail.ReceivingUpdateLockReason);
        }
    }

    [ReceivingSqlFact]
    public async Task OtherBatchInspectionAndReads_DoNotLock_StaleVersionsStillFail()
    {
        var other = await fixture.BatchAsync();
        var batch = await fixture.BatchAsync();
        await using var db = fixture.Context();
        db.QcInspections.Add(fixture.Inspection(other.ProductBatchId));
        await db.SaveChangesAsync();
        var service = new ProductBatchService(new ProductBatchQueryRepository(db), Repository(db));
        Assert.True((await service.GetAsync(batch.ProductBatchId, CancellationToken.None)).CanUpdateReceivingInformation);
        Assert.False(await db.QcInspections.AnyAsync(q => q.ProductBatchId == batch.ProductBatchId));
        Assert.Equal(ProductBatchReceivingUpdateResult.Conflict, await Repository(db).UpdateAsync(
            batch.ProductBatchId, batch.UpdatedAt, batch.CreatedAt.AddSeconds(-1), fixture.ActorId, Changes, default));
        Assert.Equal(ProductBatchReceivingUpdateResult.Updated, await Update(db, batch));
        var after = await Snapshot(batch.ProductBatchId);
        Assert.Equal(ProductBatchReceivingUpdateResult.Conflict, await Update(db, batch));
        Assert.Equal(after, await Snapshot(batch.ProductBatchId));
        Assert.Equal(1, await UpdateAuditCount(batch.ProductBatchId));
        var saved = await db.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == batch.ProductBatchId);
        Assert.Equal(batch.DeclaredQuantity, saved.DeclaredQuantity);
        Assert.Equal(batch.WeightInKg, saved.WeightInKg);
        Assert.Equal(batch.PackagingType, saved.PackagingType);
        Assert.Equal(batch.Note, saved.Note);
        Assert.Equal("PENDING_QC", saved.BatchStatus);
        Assert.Equal(Changes.VerifiedQuantity, saved.VerifiedQuantity);
        Assert.Equal(Changes.ReceivingNote, saved.ReceivingNote);
    }

    [ReceivingSqlFact]
    public Task QcInsertFirst_UpdateWaitsForCommitThenRejects() => QcFirst(rollback: false);

    [ReceivingSqlFact]
    public Task QcInsertRolledBack_WaitingUpdateSucceeds() => QcFirst(rollback: true);

    private async Task QcFirst(bool rollback)
    {
        var batch = await fixture.BatchAsync();
        var before = await Snapshot(batch.ProductBatchId);
        await using var qc = fixture.Context(retry: false);
        var qcSession = await fixture.SessionIdAsync(qc);
        await using var transaction = await qc.Database.BeginTransactionAsync();
        qc.QcInspections.Add(fixture.Inspection(batch.ProductBatchId));
        await qc.SaveChangesAsync();
        await using var receiving = fixture.Context();
        var receivingSession = await fixture.SessionIdAsync(receiving);
        var update = Update(receiving, batch);
        try { await fixture.AssertBlockedAsync(receivingSession, qcSession); }
        finally
        {
            if (rollback) await transaction.RollbackAsync();
            else await transaction.CommitAsync();
        }
        Assert.Equal(rollback ? ProductBatchReceivingUpdateResult.Updated : ProductBatchReceivingUpdateResult.QcReceived,
            await update.WaitAsync(TimeSpan.FromSeconds(20)));
        if (!rollback) Assert.Equal(before, await Snapshot(batch.ProductBatchId));
        Assert.Equal(rollback ? 1 : 0, await UpdateAuditCount(batch.ProductBatchId));
    }

    [ReceivingSqlFact]
    public async Task ReceivingUpdateFirst_QcInsertWaitsUntilUpdateAndAuditCommit()
    {
        var batch = await fixture.BatchAsync();
        var gate = new AuditGate();
        await using var receiving = fixture.Context(gate);
        var receivingSession = await fixture.SessionIdAsync(receiving);
        var update = Update(receiving, batch);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using var qc = fixture.Context();
        var qcSession = await fixture.SessionIdAsync(qc);
        qc.QcInspections.Add(fixture.Inspection(batch.ProductBatchId));
        var insert = qc.SaveChangesAsync();
        try { await fixture.AssertBlockedAsync(qcSession, receivingSession); }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(ProductBatchReceivingUpdateResult.Updated, await update.WaitAsync(TimeSpan.FromSeconds(20)));
        await insert.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, await UpdateAuditCount(batch.ProductBatchId));
        // Any further write, even with a fresh version, must be locked.
        await using var check = fixture.Context();
        var current = await check.ProductBatches.SingleAsync(b => b.ProductBatchId == batch.ProductBatchId);
        Assert.Equal(ProductBatchReceivingUpdateResult.QcReceived, await Update(check, current));
    }

    [ReceivingSqlFact]
    public async Task DirectHttpApi_EnforcesRoleAndQcLock_ForAnAlreadyOpenForm()
    {
        using var client = await fixture.ApiAsync();
        var batch = await fixture.BatchAsync();
        var path = $"/api/operation/product-batches/{batch.ProductBatchId}";
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer("QC_STAFF");
        var readAsQc = await ReadDetail(client, path);
        Assert.False(readAsQc.CanUpdateReceivingInformation);
        using var forbidden = await client.PutAsJsonAsync(path + "/receiving-details", Request(batch));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer("OPERATION_STAFF");
        Assert.True((await ReadDetail(client, path)).CanUpdateReceivingInformation);
        using var success = await client.PutAsJsonAsync(path + "/receiving-details", Request(batch));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var stale = await client.PutAsJsonAsync(path + "/receiving-details", Request(batch));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var opened = await ReadDetail(client, path);

        client.DefaultRequestHeaders.Authorization = Bearer("QC_STAFF");
        using var failed = await client.PostAsJsonAsync("/api/qc-inspections",
            new { productBatchId = batch.ProductBatchId, inspectionStandardVersionId = -1 });
        Assert.False(failed.IsSuccessStatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer("OPERATION_STAFF");
        Assert.True((await ReadDetail(client, path)).CanUpdateReceivingInformation);
        client.DefaultRequestHeaders.Authorization = Bearer("QC_STAFF");
        using var accepted = await client.PostAsJsonAsync("/api/qc-inspections",
            new { productBatchId = batch.ProductBatchId, inspectionStandardVersionId = fixture.VersionId });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var before = await Snapshot(batch.ProductBatchId);
        var auditBefore = await UpdateAuditCount(batch.ProductBatchId);
        client.DefaultRequestHeaders.Authorization = Bearer("OPERATION_STAFF");
        using var locked = await client.PutAsJsonAsync(path + "/receiving-details",
            new UpdateProductBatchReceivingRequest(13, 130, opened.UpdatedAt, opened.CreatedAt));
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProductBatchService.QcReceivingLockMessage, body.GetProperty("message").GetString());
        Assert.False((await ReadDetail(client, path)).CanUpdateReceivingInformation);
        Assert.Equal(before, await Snapshot(batch.ProductBatchId));
        Assert.Equal(auditBefore, await UpdateAuditCount(batch.ProductBatchId));
    }

    private static UpdateProductBatchReceivingRequest Request(ProductBatch b) =>
        new(12, 125, b.UpdatedAt, b.CreatedAt, "New box", 12, 10, "Changed");

    private static async Task<ProductBatchDetail> ReadDetail(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").Deserialize<ProductBatchDetail>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private AuthenticationHeaderValue Bearer(string role)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = "uc28-test", aud = "uc28-test",
            exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), account_id = fixture.ActorId.ToString(), role }));
        var signature = Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey),
            Encoding.UTF8.GetBytes(header + "." + payload)));
        return new AuthenticationHeaderValue("Bearer", header + "." + payload + "." + signature);
    }

    private sealed class AuditGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [AUDIT_LOG]"))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
