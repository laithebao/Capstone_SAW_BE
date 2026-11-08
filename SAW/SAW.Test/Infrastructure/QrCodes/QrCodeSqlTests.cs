using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Features.QrCodes.Services;
using SAW.Domain.Entities;
using SAW.Infrastructure.QrCodes;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.QrCodes;

// Reuses the isolated SQL fixture only; never touches dev batches or QC module tests.
public sealed class QrCodeSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task Worker_later_sweep_retry_and_configuration_pause_restart_use_sql_fixture()
    {
        var batch = await AcceptedAsync("E");
        var storage = new TestStorage { IsConfigured = false };
        var workerLog = new WorkerTestLog();
        var repo = new Mock<SAW.Application.Repositories.IQrCodeRepository>();
        // Candidate lookup is real SQL, restricted to this test batch so earlier fixture rows are untouched.
        repo.Setup(r => r.GetCandidatesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (long cursor, int size, CancellationToken ct) =>
            {
                await using var db = fixture.Context();
                var ids = await new QrCodeRepository(db).GetCandidatesAsync(Math.Max(cursor, batch.ProductBatchId - 1), size, ct);
                return (IReadOnlyList<long>)ids.Where(id => id == batch.ProductBatchId).ToArray();
            });
        using var provider = new ServiceCollection().AddScoped(_ => repo.Object)
            .AddScoped<IQrImageStorage>(_ => storage)
            .AddScoped<SAW.Infrastructure.Persistence.AppDbContext>(_ => fixture.Context())
            .AddScoped<IProductBatchQrCodeService>(s => Service(s.GetRequiredService<SAW.Infrastructure.Persistence.AppDbContext>(), storage))
            .BuildServiceProvider();
        QrCodeGenerationWorker Worker() => new(provider.GetRequiredService<IServiceScopeFactory>(),
            new QrCodeOptions { PublicFrontendBaseUrl = "https://saw.test.invalid", IntervalSeconds = 5, BatchSize = 100 },
            workerLog);
        using (var paused = Worker()) { await paused.StartAsync(default); await paused.StopAsync(default); }
        Assert.Contains(workerLog.Messages, text => text.Contains("worker paused: configure"));
        repo.Verify(r => r.GetCandidatesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        await using (var check = fixture.Context()) Assert.False(await check.QrCodes.AnyAsync(q => q.ProductBatchId == batch.ProductBatchId));
        storage.IsConfigured = true;
        var attempts = 0;
        storage.BeforeReturn = () => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new HttpRequestException("Isolated upload fault")) : Task.CompletedTask;
        using (var restarted = Worker())
        {
            await restarted.StartAsync(default);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    await using var db = fixture.Context();
                    if (await db.QrCodes.AnyAsync(q => q.ProductBatchId == batch.ProductBatchId, deadline.Token)) break;
                    await Task.Delay(100, deadline.Token);
                }
            }
            finally { await restarted.StopAsync(default); }
        }
        Assert.Equal(2, attempts);
        await using var saved = fixture.Context();
        var qr = Assert.Single(await saved.QrCodes.Where(q => q.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.True(qr.IsActive); Assert.Equal(64, qr.PublicToken!.Length); Assert.NotNull(qr.QrImageUrl);
        var folder = Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrEmpty(folder)) await File.WriteAllLinesAsync(Path.Combine(folder, "worker-pause-retry.log"), workerLog.Messages);
    }
    private sealed class WorkerTestLog : Microsoft.Extensions.Logging.ILogger<QrCodeGenerationWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    private async Task<ProductBatch> AcceptedAsync(string grade = "A", string status = "APPROVED_FOR_STORAGE")
    {
        var batch = await fixture.BatchAsync();
        await using var db = fixture.Context();
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.BatchStatus, status).SetProperty(b => b.QualityGrade, grade));
        var inspection = fixture.Inspection(batch.ProductBatchId, "COMPLETED");
        inspection.StartedAt = DateTime.UtcNow.AddMinutes(-10);
        inspection.CompletedAt = DateTime.UtcNow.AddMinutes(-5);
        inspection.QualityGrade = grade;
        inspection.QcResult = "PASS";
        db.QcInspections.Add(inspection);
        await db.SaveChangesAsync();
        batch.BatchStatus = status;
        batch.QualityGrade = grade;
        return batch;
    }

    private ProductBatchQrCodeService Service(SAW.Infrastructure.Persistence.AppDbContext db, IQrImageStorage storage) =>
        new(new QrCodeRepository(db), new PngQrCodeRenderer(), storage,
            new QrCodeOptions { PublicFrontendBaseUrl = "https://saw.test.invalid" }, NullLogger<ProductBatchQrCodeService>.Instance);

    [ReceivingSqlFact]
    public async Task TwoGenerators_UploadConcurrently_CommitExactlyOneQrAndAudit_AndCleanOnlyLoser()
    {
        var batch = await AcceptedAsync();
        var storage = new TestStorage();
        var bothUploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        storage.BeforeReturn = async () =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) bothUploaded.TrySetResult();
            await bothUploaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        };
        await using var db1 = fixture.Context();
        await using var db2 = fixture.Context();
        await Task.WhenAll(Service(db1, storage).GenerateAsync(batch.ProductBatchId, default),
            Service(db2, storage).GenerateAsync(batch.ProductBatchId, default)).WaitAsync(TimeSpan.FromSeconds(30));
        await using var check = fixture.Context();
        var qr = await check.QrCodes.SingleAsync(q => q.ProductBatchId == batch.ProductBatchId);
        Assert.True(qr.IsActive);
        Assert.Null(qr.PackageCode);
        var loser = Assert.Single(storage.Deleted);
        Assert.NotEqual(storage.Url(loser), qr.QrImageUrl);
        Assert.Equal(2, storage.Uploaded.Count);
        var audits = await check.AuditLogs.Where(a => a.EntityName == "QR_CODE" && a.NewDataJson!.Contains(qr.PublicToken)).ToListAsync();
        Assert.Null(Assert.Single(audits).AccountId);
        Assert.Empty(await check.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId).ToListAsync());
        var before = await check.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == batch.ProductBatchId);
        await Service(db1, storage).GenerateAsync(batch.ProductBatchId, default);
        var stable = await check.QrCodes.AsNoTracking().SingleAsync(q => q.ProductBatchId == batch.ProductBatchId);
        Assert.Equal(qr.PublicToken, stable.PublicToken);
        Assert.Equal(qr.TraceabilityUrl, stable.TraceabilityUrl);
        Assert.Equal(qr.GeneratedAt, stable.GeneratedAt);
        Assert.Equal(2, storage.Uploaded.Count);
        Assert.Equal(batch.UpdatedAt, before.UpdatedAt);
        Assert.Equal(batch.DeclaredQuantity, before.DeclaredQuantity);
        Assert.Equal(batch.VerifiedQuantity, before.VerifiedQuantity);
        Assert.Equal(batch.BatchStatus, before.BatchStatus);
    }

    [ReceivingSqlFact]
    public async Task SqlUniqueFilter_ProtectsNullPackage_WhileAllowingInactiveAndPackageRows()
    {
        var batch = await AcceptedAsync();
        static QrCode Code(long id, bool active = true, string? package = null) => new()
        {
            ProductBatchId = id, IsActive = active, PackageCode = package, GeneratedAt = DateTime.UtcNow,
            PublicToken = Guid.NewGuid().ToString("N"), TraceabilityUrl = "https://saw.test/trace/" + Guid.NewGuid().ToString("N")
        };
        await using var db = fixture.Context();
        db.QrCodes.Add(Code(batch.ProductBatchId));
        await db.SaveChangesAsync();
        await using var duplicate = fixture.Context();
        duplicate.QrCodes.Add(Code(batch.ProductBatchId));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        Assert.Contains(((SqlException)error.InnerException!).Number, new[] { 2601, 2627 });
        Assert.Contains("UQ_QR_CODE_ActiveBatch", error.InnerException.Message);
        db.QrCodes.AddRange(Code(batch.ProductBatchId, false), Code(batch.ProductBatchId, true, "Package-1"));
        await db.SaveChangesAsync();
        Assert.Equal(3, await db.QrCodes.CountAsync(q => q.ProductBatchId == batch.ProductBatchId));
    }

    [ReceivingSqlFact]
    public async Task LatestInspectionOfAnyStatus_PreventsUsingOldPass_AndCandidateQueryIsBounded()
    {
        var first = await AcceptedAsync();
        var second = await AcceptedAsync();
        await using var db = fixture.Context();
        db.QcInspections.Add(fixture.Inspection(first.ProductBatchId));
        var failed = fixture.Inspection(second.ProductBatchId, "COMPLETED");
        failed.QcResult = "FAIL"; failed.QualityGrade = "E"; failed.CompletedAt = DateTime.UtcNow.AddSeconds(1);
        db.QcInspections.Add(failed);
        await db.SaveChangesAsync();
        var repo = new QrCodeRepository(db);
        Assert.Equal("DRAFT", (await repo.GetAsync(first.ProductBatchId, default))!.LatestInspection!.InspectionStatus);
        var storage = new TestStorage();
        await Service(db, storage).GenerateAsync(first.ProductBatchId, default);
        await Service(db, storage).GenerateAsync(second.ProductBatchId, default);
        Assert.Empty(storage.Uploaded);
        var dQuarantine = await AcceptedAsync("D", "QUARANTINE");
        var dApproved = await AcceptedAsync("D");
        var candidates = await repo.GetCandidatesAsync(first.ProductBatchId - 1, 100, default);
        Assert.DoesNotContain(first.ProductBatchId, candidates);
        Assert.DoesNotContain(second.ProductBatchId, candidates);
        Assert.DoesNotContain(dQuarantine.ProductBatchId, candidates);
        Assert.Contains(dApproved.ProductBatchId, candidates);
        Assert.Single(await repo.GetCandidatesAsync(dApproved.ProductBatchId - 1, 1, default));
    }

    [ReceivingSqlFact]
    public async Task EligibilityLostDuringUpload_DiscardsOwnImageWithoutQrOrAudit()
    {
        var batch = await AcceptedAsync();
        var storage = new TestStorage
        {
            BeforeReturn = async () =>
            {
                await using var other = fixture.Context();
                await other.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.BatchStatus, "REJECTED"));
            }
        };
        await using var db = fixture.Context();
        var auditBefore = await db.AuditLogs.CountAsync(a => a.EntityName == "QR_CODE");
        await Service(db, storage).GenerateAsync(batch.ProductBatchId, default);
        Assert.Empty(await db.QrCodes.Where(q => q.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Equal(auditBefore, await db.AuditLogs.CountAsync(a => a.EntityName == "QR_CODE"));
        Assert.Equal(Assert.Single(storage.Uploaded), Assert.Single(storage.Deleted));
    }

    [ReceivingSqlFact]
    public async Task NewQcOrDisabledQrDuringUpload_IsRecheckedAtCommit()
    {
        foreach (var disabledQr in new[] { false, true })
        {
            var batch = await AcceptedAsync();
            var storage = new TestStorage
            {
                BeforeReturn = async () =>
                {
                    await using var other = fixture.Context();
                    if (disabledQr)
                        other.QrCodes.Add(new QrCode { ProductBatchId = batch.ProductBatchId, IsActive = false,
                            PublicToken = Guid.NewGuid().ToString("N"), TraceabilityUrl = "https://test.invalid/" + Guid.NewGuid().ToString("N") });
                    else
                        other.QcInspections.Add(fixture.Inspection(batch.ProductBatchId));
                    await other.SaveChangesAsync();
                }
            };
            await using var db = fixture.Context();
            await Service(db, storage).GenerateAsync(batch.ProductBatchId, default);
            Assert.False(await db.QrCodes.AnyAsync(q => q.ProductBatchId == batch.ProductBatchId && q.IsActive));
            Assert.Equal(Assert.Single(storage.Uploaded), Assert.Single(storage.Deleted));
        }
    }

    [ReceivingSqlFact]
    public async Task RepairPreservesIdentity_AndInactiveRecordCannotBeBypassed()
    {
        var batch = await AcceptedAsync();
        await using var db = fixture.Context();
        var qr = new QrCode { ProductBatchId = batch.ProductBatchId, PublicToken = Guid.NewGuid().ToString("N"),
            TraceabilityUrl = "https://previous-host/trace/" + Guid.NewGuid().ToString("N"),
            GeneratedAt = new DateTime(2026, 1, 1), IsActive = true };
        db.QrCodes.Add(qr);
        await db.SaveChangesAsync();
        var storage = new TestStorage();
        await Service(db, storage).GenerateAsync(batch.ProductBatchId, default);
        var repaired = await db.QrCodes.AsNoTracking().SingleAsync(q => q.QrCodeId == qr.QrCodeId);
        Assert.Equal(qr.PublicToken, repaired.PublicToken);
        Assert.Equal(qr.TraceabilityUrl, repaired.TraceabilityUrl);
        Assert.Equal(qr.GeneratedAt, repaired.GeneratedAt);
        Assert.NotNull(repaired.QrImageUrl);
        await db.QrCodes.Where(q => q.QrCodeId == qr.QrCodeId).ExecuteUpdateAsync(s => s.SetProperty(q => q.IsActive, false));
        await Service(db, storage).GenerateAsync(batch.ProductBatchId, default);
        Assert.Equal("UNAVAILABLE", (await Service(db, storage).GetAsync(batch.ProductBatchId, default)).Status);
        Assert.Single(storage.Uploaded);
        Assert.Single(await db.QrCodes.Where(q => q.ProductBatchId == batch.ProductBatchId).ToListAsync());
    }

    [ReceivingSqlFact]
    public async Task RealHttpGet_EnforcesInternalRoles_AndNeverGeneratesOrAudits()
    {
        var batch = await AcceptedAsync();
        using var client = await fixture.ApiAsync();
        var path = $"/api/operation/product-batches/{batch.ProductBatchId}/qr-code";
        await using var db = fixture.Context();
        var auditBefore = await db.AuditLogs.CountAsync();
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var role in new[] { "SUPPLIER", "DISTRIBUTOR", "OPERATION_STAFF", "QC_STAFF", "WAREHOUSE_MANAGER", "ADMINISTRATOR" })
        {
            client.DefaultRequestHeaders.Authorization = Bearer(role);
            using var response = await client.GetAsync(path);
            Assert.Equal(role is "SUPPLIER" or "DISTRIBUTOR" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
            if (response.IsSuccessStatusCode)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("PENDING", body.RootElement.GetProperty("data").GetProperty("status").GetString());
            }
        }
        Assert.Empty(await db.QrCodes.Where(q => q.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Equal(auditBefore, await db.AuditLogs.CountAsync());
    }

    private AuthenticationHeaderValue Bearer(string role)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = "uc28-test", aud = "uc28-test",
            exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), account_id = fixture.ActorId.ToString(), role }));
        var signature = Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey),
            Encoding.UTF8.GetBytes(header + "." + payload)));
        return new("Bearer", header + "." + payload + "." + signature);
    }

    private sealed class TestStorage : IQrImageStorage
    {
        public bool IsConfigured { get; set; } = true;
        public ConcurrentBag<string> Uploaded { get; } = [];
        public ConcurrentBag<string> Deleted { get; } = [];
        public Func<Task>? BeforeReturn { get; set; }
        public string Url(string id) => "https://cloudinary.test.invalid/" + id + ".png";
        public async Task<string> UploadAsync(string id, byte[] png, CancellationToken ct)
        {
            Uploaded.Add(id);
            if (BeforeReturn is not null) await BeforeReturn();
            return Url(id);
        }
        public Task DeleteAsync(string id, CancellationToken ct) { Deleted.Add(id); return Task.CompletedTask; }
    }
}
