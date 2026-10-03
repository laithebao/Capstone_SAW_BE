using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Features.QrCodes.Services;
using SAW.Application.Features.Traceability.Services;
using SAW.Infrastructure.QrCodes;
using SAW.Application.Exceptions;
using SAW.Application.Features.QcInspections;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.QcInspections;

public sealed class QcFinalizationSqlFixture : IAsyncLifetime
{
    public ReceivingSqlFixture Database { get; } = new();
    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        var source = Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION");
        if (string.IsNullOrEmpty(source)) return;
        try
        {
            await using var connection = new SqlConnection(source);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name IN ('TR_QC_INSPECTION_COMPLETED_IMMUTABLE','TR_INSPECTION_RESULT_DETAIL_IMMUTABLE','TR_INSPECTION_RESULT_DETAIL_VERSION_CHECK','TR_PRODUCT_BATCH_STATUS_HISTORY','TR_BATCH_STATUS_HISTORY_IMMUTABLE','TR_AUDIT_LOG_IMMUTABLE')";
            var definitions = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));
            Assert.Equal(6, definitions.Count);
            await using var db = Database.Context();
            foreach (var sql in definitions) await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch { await Database.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Database.DisposeAsync();
}

public sealed class QcFinalizationSqlTests(QcFinalizationSqlFixture fixture) : IClassFixture<QcFinalizationSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Database;
    private AppDbContext Context(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString,
            sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(20), null));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("account_id", F.ActorId.ToString())], "test")) } };
        return new AppDbContext(options.Options, accessor);
    }

    private async Task<(long BatchId, long InspectionId)> Prepare(string grade, bool criticalFailure = false)
    {
        var batch = await F.BatchAsync();
        await using var db = Context();
        var setId = await db.InspectionStandardVersions.Where(v => v.InspectionStandardVersionId == F.VersionId).Select(v => v.InspectionStandardSetId).SingleAsync();
        var version = new InspectionStandardVersion { InspectionStandardSetId = setId, VersionNo = 100 + await db.InspectionStandardVersions.CountAsync(), VersionStatus = "PUBLISHED" };
        var criterion = new InspectionCriterion { CriterionCode = "GRADE", CriterionName = "Measured grade", CriterionGroup = "SENSORY", DataType = "NUMBER", IsRequired = true,
            GradeRules = [new CriterionGradeRule { Grade = grade, MinValue = 0, MaxValue = 10, IsFailRule = false }] };
        version.Criteria.Add(criterion);
        var q = F.Inspection(batch.ProductBatchId, "IN_PROGRESS");
        q.InspectionStandardVersion = version; q.InspectionStandardVersionId = 0;
        q.ResultDetails.Add(new InspectionResultDetail { InspectionCriterion = criterion, NumericValue = 5, Remarks = "Actual measurement", IsPassed = true });
        if (criticalFailure)
        {
            var gate = new InspectionCriterion { CriterionCode = "SAFETY", CriterionName = "Safety gate", CriterionGroup = "LAB", DataType = "BOOLEAN", IsRequired = true, IsCritical = true };
            version.Criteria.Add(gate);
            q.ResultDetails.Add(new InspectionResultDetail { InspectionCriterion = gate, BooleanValue = false, IsPassed = true });
        }
        db.QcInspections.Add(q); await db.SaveChangesAsync();
        return (batch.ProductBatchId, q.QcInspectionId);
    }

    private async Task<FinalizeQcResultDto> Finalize(long id, IInterceptor? interceptor = null)
    {
        await using var db = Context(interceptor);
        return await new QcInspectionService(new QcInspectionRepository(db)).FinalizeAsync(id, F.ActorId, "QC_STAFF", default);
    }

    [ReceivingSqlFact]
    public async Task All_grades_with_real_immutable_triggers_and_downstream_eligibility()
    {
        foreach (var grade in new[] { "A", "B", "C", "D", "E" })
        {
            var (batchId, id) = await Prepare(grade);
            var result = await Finalize(id);
            Assert.Equal(grade == "E" ? "FAIL" : "PASS", result.QcResult);
            Assert.Equal(grade == "E" ? "REJECTED" : "APPROVED_FOR_STORAGE", result.NewBatchStatus);
            await using var db = Context();
            var batch = await db.ProductBatches.SingleAsync(b => b.ProductBatchId == batchId);
            var q = await db.QcInspections.Include(q => q.ResultDetails).SingleAsync(q => q.QcInspectionId == id);
            Assert.Equal(grade, batch.QualityGrade); Assert.Equal(grade, q.QualityGrade);
            var detail = Assert.Single(q.ResultDetails);
            Assert.Equal(grade, detail.EvaluatedGrade); Assert.Equal(grade != "E", detail.IsPassed); Assert.Equal("Actual measurement", detail.Remarks);
            Assert.Equal(10, batch.VerifiedQuantity); Assert.Equal(100, batch.VerifiedWeightInKg);
            Assert.Empty(await db.Inventories.Where(i => i.ProductBatchId == batchId).ToListAsync());
            Assert.Empty(await db.GoodsReceipts.Where(r => r.ProductBatchId == batchId).ToListAsync());
            var history = Assert.Single(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batchId && h.OldStatus == "PENDING_QC").ToListAsync());
            Assert.Equal(F.ActorId, history.ChangedByAccountId);
            var audit = Assert.Single(await db.AuditLogs.Where(a => a.EntityName == "QC_INSPECTION" && a.EntityId == "QcInspectionId=" + id && a.ActionType == "UPDATE").ToListAsync());
            Assert.Equal(F.ActorId, audit.AccountId);
            var qrCandidates = await new QrCodeRepository(db).GetCandidatesAsync(batchId - 1, 1, default);
            Assert.Equal(grade != "E", qrCandidates.Contains(batchId));
            var eligible = await new GoodsReceiptQueryRepository(db).EligibleAsync(null, batch.BatchCode, 1, 10, default);
            Assert.Equal(grade != "E", eligible.Items.Any(b => b.Id == batchId));
            var storage = new TestQrStorage();
            var qrService = new ProductBatchQrCodeService(new QrCodeRepository(db), new PngQrCodeRenderer(), storage,
                new QrCodeOptions { PublicFrontendBaseUrl = "https://saw.test.invalid" }, NullLogger<ProductBatchQrCodeService>.Instance);
            await qrService.GenerateAsync(batchId, default);
            Assert.Equal(grade != "E", storage.Uploaded);
            var location = new WarehouseLocation { LocationCode = Guid.NewGuid().ToString("N"), ZoneName = "QC test", LocationStatus = "ACTIVE" };
            db.WarehouseLocations.Add(location); await db.SaveChangesAsync();
            var receiptRepo = new GoodsReceiptRepository(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString, sql => sql.EnableRetryOnFailure()).Options, new HttpContextAccessor());
            var request = new SAW.Application.Features.GoodsReceipts.Dtos.CreateGoodsReceiptRequest(batchId, location.WarehouseLocationId, DateOnly.FromDateTime(DateTime.UtcNow), null);
            if (grade == "E") await Assert.ThrowsAsync<ConflictException>(() => receiptRepo.CreateAsync(request, F.ActorId, default));
            else
            {
                var qr = await db.QrCodes.SingleAsync(q => q.ProductBatchId == batchId);
                var trace = await new TraceabilityService(new TraceabilityRepository(db)).GetAsync(qr.PublicToken, default);
                Assert.Equal(grade, trace.Quality.Grade); Assert.Equal("PASS", trace.Quality.Result);
                await qrService.GenerateAsync(batchId, default);
                Assert.Single(await db.QrCodes.Where(q => q.ProductBatchId == batchId).ToListAsync());
                Assert.True(await receiptRepo.CreateAsync(request, F.ActorId, default) > 0);
            }
            await Assert.ThrowsAsync<BadRequestException>(() => Finalize(id));
            await Assert.ThrowsAsync<SqlException>(() => db.QcInspections.Where(q => q.QcInspectionId == id).ExecuteUpdateAsync(s => s.SetProperty(q => q.Note, "forbidden")));
            await Assert.ThrowsAsync<SqlException>(() => db.InspectionResultDetails.Where(d => d.QcInspectionId == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.NumericValue, 2m)));
        }
    }

    private sealed class TestQrStorage : IQrImageStorage
    {
        public bool IsConfigured => true;
        public bool Uploaded { get; private set; }
        public Task<string> UploadAsync(string id, byte[] png, CancellationToken ct)
        {
            Assert.True(png.Length > 100); Uploaded = true;
            return Task.FromResult("https://saw.test.invalid/qr/" + id + ".png");
        }
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    [ReceivingSqlFact]
    public async Task Serious_defect_overrides_A_but_does_not_rewrite_individual_grades()
    {
        var (batchId, id) = await Prepare("A", true);
        var result = await Finalize(id);
        Assert.Equal("E", result.QualityGrade); Assert.Equal("FAIL", result.QcResult); Assert.Equal("REJECTED", result.NewBatchStatus);
        await using var db = Context();
        var details = await db.InspectionResultDetails.Where(d => d.QcInspectionId == id).ToListAsync();
        Assert.Equal("A", details.Single(d => d.NumericValue != null).EvaluatedGrade);
        Assert.False(details.Single(d => d.BooleanValue != null).IsPassed);
        Assert.Contains("SAFETY", (await db.ProductBatches.SingleAsync(b => b.ProductBatchId == batchId)).RejectionReason);
    }

    private sealed class FailCommit(bool afterCommit) : DbTransactionInterceptor
    {
        private int failures;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && Interlocked.Increment(ref failures) == 1) throw new TimeoutException("Test rollback");
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (afterCommit && Interlocked.Increment(ref failures) == 1) throw new TimeoutException("Test unknown commit result");
            return Task.CompletedTask;
        }
    }

    [ReceivingSqlFact]
    public async Task Missing_rules_and_failure_in_header_write_leave_no_partial_results_or_audit()
    {
        var (batchId, id) = await Prepare("D");
        await using var db = Context();
        var before = await db.AuditLogs.CountAsync();
        var ddl = "ALTER TABLE PRODUCT_BATCH ADD CONSTRAINT CK_QC_TEST_FAILURE CHECK (ProductBatchID <> "
            + batchId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " OR BatchStatus='PENDING_QC')";
        await db.Database.ExecuteSqlRawAsync(ddl);
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Finalize(id)); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE PRODUCT_BATCH DROP CONSTRAINT CK_QC_TEST_FAILURE"); }
        Assert.Equal(before, await db.AuditLogs.CountAsync());
        Assert.Equal("IN_PROGRESS", (await db.QcInspections.AsNoTracking().SingleAsync(q => q.QcInspectionId == id)).InspectionStatus);
        Assert.Null((await db.InspectionResultDetails.AsNoTracking().SingleAsync(d => d.QcInspectionId == id)).EvaluatedGrade);
        Assert.Empty(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batchId && h.OldStatus == "PENDING_QC").ToListAsync());
        var criterionId = await db.InspectionResultDetails.Where(d => d.QcInspectionId == id).Select(d => d.InspectionCriterionId).SingleAsync();
        await db.CriterionGradeRules.Where(r => r.InspectionCriterionId == criterionId).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<BadRequestException>(() => Finalize(id));
        Assert.Equal(before, await db.AuditLogs.CountAsync());
    }

    [ReceivingSqlFact]
    public async Task Retry_and_concurrent_finalization_never_duplicate_history_or_audit()
    {
        foreach (var afterCommit in new[] { false, true })
        {
            var (batchId, id) = await Prepare("D");
            await Finalize(id, new FailCommit(afterCommit));
            await using var db = Context();
            Assert.Single(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batchId && h.OldStatus == "PENDING_QC").ToListAsync());
            Assert.Single(await db.AuditLogs.Where(a => a.EntityName == "QC_INSPECTION" && a.EntityId == "QcInspectionId=" + id && a.ActionType == "UPDATE").ToListAsync());
        }
        var (batch, inspection) = await Prepare("D");
        async Task<bool> Attempt() { try { await Finalize(inspection); return true; } catch (BadRequestException) { return false; } }
        Assert.Single(await Task.WhenAll(Attempt(), Attempt()), x => x);
        await using var check = Context();
        Assert.Single(await check.BatchStatusHistories.Where(h => h.ProductBatchId == batch && h.OldStatus == "PENDING_QC").ToListAsync());
    }
}
