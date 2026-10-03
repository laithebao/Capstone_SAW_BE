using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Features.GoodsReceipts.Services;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.GoodsReceipts;

public sealed class GoodsReceiptBrowserFactAttribute : FactAttribute
{
    public GoodsReceiptBrowserFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION"))
            || Environment.GetEnvironmentVariable("SAW_TEST_BROWSER") != "1")
            Skip = "Set SAW_TEST_SQL_CONNECTION and SAW_TEST_BROWSER=1 for Edge/API/SQL integration.";
    }
}

// Reuses the isolated database lifecycle, never writes to the supplied development database.
public sealed class GoodsReceiptSqlFixture : IAsyncLifetime
{
    public ReceivingSqlFixture Database { get; } = new();
    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        var source = Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION");
        if (string.IsNullOrEmpty(source)) return;
        try
        {
            await using var live = new SqlConnection(source);
            await live.OpenAsync();
            await using var command = live.CreateCommand();
            command.CommandText = "SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name IN ('TR_GOODS_RECEIPT_COMMITTED_IMMUTABLE','TR_PRODUCT_BATCH_STATUS_HISTORY','TR_BATCH_STATUS_HISTORY_IMMUTABLE')";
            var definitions = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));
            Assert.Equal(3, definitions.Count); // Test actual DB trigger bodies, not approximations.
            await using var db = Database.Context();
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE INVENTORY DROP COLUMN AvailableQuantity;
                ALTER TABLE INVENTORY ADD AvailableQuantity AS (QuantityOnHand - ReservedQuantity) PERSISTED;
                ALTER TABLE INVENTORY ADD CONSTRAINT CK_INVENTORY_Unit CHECK (Unit=N'kg');
                ALTER TABLE INVENTORY ADD CONSTRAINT CK_INVENTORY_Quantity CHECK (QuantityOnHand>=0 AND ReservedQuantity>=0 AND ReservedQuantity<=QuantityOnHand);
                ALTER TABLE GOODS_RECEIPT ADD CONSTRAINT CK_GOODS_RECEIPT_Status CHECK (ReceiptStatus IN ('DRAFT','COMMITTED'));
                ALTER TABLE GOODS_RECEIPT ADD CONSTRAINT CK_GOODS_RECEIPT_Quantity CHECK (ReceivedQuantity>0 AND WeightInKg>0);
                CREATE INDEX IX_GOODS_RECEIPT_Batch_Date ON GOODS_RECEIPT(ProductBatchID, ReceivedAt);
                """);
            foreach (var definition in definitions) await db.Database.ExecuteSqlRawAsync(definition);
        }
        catch { await Database.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Database.DisposeAsync();
}

public sealed class GoodsReceiptSqlTests(GoodsReceiptSqlFixture fixture) : IClassFixture<GoodsReceiptSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Database;
    private IHttpContextAccessor Accessor => new HttpContextAccessor { HttpContext = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", F.ActorId.ToString()), new Claim(ClaimTypes.Role, "OPERATION_STAFF")], "test"))
    } };
    private GoodsReceiptRepository Repository() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(F.ConnectionString, o => o.EnableRetryOnFailure(3)).Options, Accessor);
    private GoodsReceiptService Service(AppDbContext db) => new(new GoodsReceiptQueryRepository(db), Repository());
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
    private async Task ConfirmAsync(GoodsReceiptRepository repo, long id)
    {
        await using var db = F.Context();
        var r = await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.GoodsReceiptId == id);
        await repo.ConfirmAsync(id, new(r.WarehouseLocationId, r.ReceivedAt, r.Note), F.ActorId, default);
    }

    private async Task<(ProductBatch Batch, WarehouseLocation Location)> EligibleAsync(decimal weight = 1000, decimal? capacity = null)
    {
        var batch = await F.BatchAsync();
        await using var db = F.Context();
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.BatchStatus, "APPROVED_FOR_STORAGE").SetProperty(b => b.QualityGrade, "B")
            .SetProperty(b => b.VerifiedQuantity, 100m).SetProperty(b => b.VerifiedWeightInKg, weight).SetProperty(b => b.Unit, "Bao"));
        var qc = F.Inspection(batch.ProductBatchId, "COMPLETED"); qc.CompletedAt = DateTime.UtcNow; qc.QcResult = "PASS"; qc.QualityGrade = "B";
        var location = new WarehouseLocation { LocationCode = Guid.NewGuid().ToString("N"), ZoneName = "Receipt test", LocationStatus = "ACTIVE", MaxWeightKg = capacity };
        db.AddRange(qc, location); await db.SaveChangesAsync();
        return (batch, location);
    }
    private async Task<long> DraftAsync(ProductBatch batch, WarehouseLocation location) => await Repository()
        .CreateAsync(new(batch.ProductBatchId, location.WarehouseLocationId, Today, "Test receipt"), F.ActorId, default);

    private async Task<GoodsReceiptSnapshot> SnapshotAsync(long id)
    {
        await using var db = F.Context();
        var r = await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.GoodsReceiptId == id);
        return new(r.WarehouseLocationId, r.ReceivedAt, r.Note);
    }

    [ReceivingSqlFact]
    public async Task Edit_whitelist_audit_null_and_stale_edit_or_confirm_are_protected()
    {
        var (batch, location) = await EligibleAsync(); var id = await DraftAsync(batch, location);
        var (_, destination) = await EligibleAsync(); var old = await SnapshotAsync(id);
        await using var db = F.Context();
        var before = await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.GoodsReceiptId == id);
        var history = await db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == batch.ProductBatchId);
        var request = new UpdateGoodsReceiptDraftRequest(destination.WarehouseLocationId, Today.AddDays(-1), "  edited  ", old);
        var updated = await Service(db).UpdateDraftAsync(id, request, F.ActorId, default);
        Assert.Equal("edited", updated.Note);
        var after = await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.GoodsReceiptId == id);
        Assert.Equal(before.ProductBatchId, after.ProductBatchId); Assert.Equal(before.ReceiptCode, after.ReceiptCode);
        Assert.Equal(before.OperationAccountId, after.OperationAccountId); Assert.Equal(before.ReceivedQuantity, after.ReceivedQuantity);
        Assert.Equal(before.Unit, after.Unit); Assert.Equal(before.WeightInKg, after.WeightInKg);
        Assert.Equal("DRAFT", after.ReceiptStatus); Assert.Null(after.CommittedAt);
        Assert.Equal(destination.WarehouseLocationId, after.WarehouseLocationId);
        Assert.Equal(Today.AddDays(-1).ToDateTime(TimeOnly.MinValue), after.ReceivedAt);
        Assert.Empty(await db.Inventories.Where(i => i.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Equal("APPROVED_FOR_STORAGE", (await db.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == batch.ProductBatchId)).BatchStatus);
        Assert.Equal(history, await db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == batch.ProductBatchId));
        var audit = Assert.Single(await db.AuditLogs.Where(a => a.EntityName == "GOODS_RECEIPT" && a.EntityId == "GoodsReceiptId=" + id && a.ActionType == "UPDATE").ToListAsync());
        Assert.Equal(F.ActorId, audit.AccountId); Assert.Contains("Test receipt", audit.OldDataJson); Assert.Contains("edited", audit.NewDataJson);
        var count = await db.AuditLogs.CountAsync();
        await Assert.ThrowsAsync<ConflictException>(() => Repository().UpdateDraftAsync(id, request, F.ActorId, default));
        await Assert.ThrowsAsync<ConflictException>(() => Repository().ConfirmAsync(id, old, F.ActorId, default));
        Assert.Equal(count, await db.AuditLogs.CountAsync());
        await Service(db).UpdateDraftAsync(id, request with { ExpectedSnapshot = updated.Snapshot, Note = "  " }, F.ActorId, default);
        Assert.Null((await SnapshotAsync(id)).Note);
        await ConfirmAsync(Repository(), id);
        Assert.Equal(destination.WarehouseLocationId, (await db.Inventories.SingleAsync(i => i.ProductBatchId == batch.ProductBatchId)).WarehouseLocationId);
        var committedSnapshot = await SnapshotAsync(id);
        await Assert.ThrowsAsync<ConflictException>(() => Repository().UpdateDraftAsync(id, request with { ExpectedSnapshot = committedSnapshot }, F.ActorId, default));
    }

    private sealed class PauseCommitInterceptor : DbTransactionInterceptor
    {
        public int SessionId { get; private set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            SessionId = ((SqlConnection)transaction.Connection!).ServerProcessId;
            Ready.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    private sealed class ReceiptReadInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GOODS_RECEIPT")) Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
            return ValueTask.FromResult(result);
        }
    }

    [ReceivingSqlFact]
    public async Task Edit_and_confirm_compete_in_both_commit_orders_on_sql_server()
    {
        foreach (var editFirst in new[] { true, false })
        {
            var (batch, location) = await EligibleAsync(); var id = await DraftAsync(batch, location);
            var old = await SnapshotAsync(id);
            var request = new UpdateGoodsReceiptDraftRequest(location.WarehouseLocationId, Today, "new reviewed note", old);
            var pause = new PauseCommitInterceptor();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString,
                sql => sql.EnableRetryOnFailure(3)).AddInterceptors(pause).Options;
            var firstRepo = new GoodsReceiptRepository(options, Accessor);
            var first = editFirst ? firstRepo.UpdateDraftAsync(id, request, F.ActorId, default)
                : firstRepo.ConfirmAsync(id, old, F.ActorId, default);
            await pause.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var read = new ReceiptReadInterceptor();
            var secondRepo = new GoodsReceiptRepository(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(F.ConnectionString, sql => sql.EnableRetryOnFailure(3)).AddInterceptors(read).Options, Accessor);
            var second = editFirst ? secondRepo.ConfirmAsync(id, old, F.ActorId, default)
                : secondRepo.UpdateDraftAsync(id, request, F.ActorId, default);
            try { await F.AssertBlockedAsync(await read.Session.Task.WaitAsync(TimeSpan.FromSeconds(10)), pause.SessionId); }
            finally { pause.Release.TrySetResult(); }
            await first;
            await Assert.ThrowsAsync<ConflictException>(() => second);
            await using var db = F.Context();
            var r = await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.GoodsReceiptId == id);
            Assert.Equal(editFirst ? "DRAFT" : "COMMITTED", r.ReceiptStatus);
            Assert.Equal(editFirst ? "new reviewed note" : old.Note, r.Note);
            Assert.Equal(editFirst ? 0 : 1000, await db.Inventories.Where(i => i.ProductBatchId == batch.ProductBatchId).SumAsync(i => i.QuantityOnHand));
            Assert.Single(await db.AuditLogs.Where(a => a.EntityName == "GOODS_RECEIPT" && a.EntityId == "GoodsReceiptId=" + id && a.ActionType == "UPDATE").ToListAsync());
            if (editFirst) await ConfirmAsync(Repository(), id);
        }
    }

    [GoodsReceiptBrowserFact]
    public async Task Browser_draft_list_reopen_confirm_and_inventory()
    {
        var (batch, location) = await EligibleAsync();
        using var client = await F.ApiAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Capstone_SAW_FE"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node") { WorkingDirectory = Path.Combine(root.FullName, "Capstone_SAW_FE"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("tests/goods-receipts-browser.mjs");
        start.Environment["SAW_TEST_API_URL"] = client.BaseAddress!.ToString();
        start.Environment["SAW_TEST_BATCH_ID"] = batch.ProductBatchId.ToString();
        start.Environment["SAW_TEST_BATCH_CODE"] = batch.BatchCode;
        start.Environment["SAW_TEST_SUPPLIER_ID"] = F.SupplierId.ToString();
        start.Environment["SAW_TEST_LOCATION_ID"] = location.WarehouseLocationId.ToString();
        start.Environment["SAW_TEST_TOKEN"] = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test", "uc28-test",
            [new Claim("account_id", F.ActorId.ToString()), new Claim(ClaimTypes.Role, "OPERATION_STAFF")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        await using var db = F.Context();
        var receipt = await db.GoodsReceipts.SingleAsync(r => r.ProductBatchId == batch.ProductBatchId);
        Assert.Equal("COMMITTED", receipt.ReceiptStatus); Assert.Equal("Browser receiving test", receipt.Note);
        Assert.Equal(1000, (await db.Inventories.SingleAsync(i => i.ProductBatchId == batch.ProductBatchId)).QuantityOnHand);
    }

    [ReceivingSqlFact]
    public async Task Draft_then_commit_uses_verified_kg_once_and_preserves_supplier_and_reserved_data()
    {
        var (batch, location) = await EligibleAsync();
        var id = await DraftAsync(batch, location);
        await using var db = F.Context();
        Assert.Empty(await db.Inventories.Where(i => i.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Equal("APPROVED_FOR_STORAGE", (await db.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == batch.ProductBatchId)).BatchStatus);
        var draft = await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.GoodsReceiptId == id);
        Assert.Equal(100, draft.ReceivedQuantity); Assert.Equal(1000, draft.WeightInKg); Assert.Equal("Bao", draft.Unit); Assert.Null(draft.CommittedAt);
        db.Inventories.Add(new Inventory { ProductBatchId = batch.ProductBatchId, WarehouseLocationId = location.WarehouseLocationId, QuantityOnHand = 30, ReservedQuantity = 5, Unit = "kg" });
        await db.SaveChangesAsync();
        await ConfirmAsync(Repository(), id);
        var auditCount = await db.AuditLogs.CountAsync();
        var historyCount = await db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == batch.ProductBatchId);
        await ConfirmAsync(Repository(), id);
        Assert.Equal(auditCount, await db.AuditLogs.CountAsync());
        Assert.Equal(historyCount, await db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == batch.ProductBatchId));
        var stock = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductBatchId == batch.ProductBatchId);
        Assert.Equal(1030, stock.QuantityOnHand); Assert.Equal(5, stock.ReservedQuantity); Assert.Equal(1025, stock.AvailableQuantity); Assert.Equal("kg", stock.Unit);
        var after = await db.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == batch.ProductBatchId);
        Assert.Equal("IN_STOCK", after.BatchStatus); Assert.Equal(batch.DeclaredQuantity, after.DeclaredQuantity); Assert.Equal(batch.WeightInKg, after.WeightInKg);
        Assert.Equal("Supplier note", after.Note);
        var history = await db.BatchStatusHistories.SingleAsync(h => h.ProductBatchId == batch.ProductBatchId && h.NewStatus == "IN_STOCK");
        Assert.Equal(F.ActorId, history.ChangedByAccountId);
        Assert.Contains(await db.AuditLogs.Where(a => a.EntityName == "GOODS_RECEIPT" && a.ActionType == "UPDATE").ToListAsync(), a => a.AccountId == F.ActorId && a.NewDataJson!.Contains("COMMITTED"));
        await Assert.ThrowsAsync<SqlException>(() => db.GoodsReceipts.Where(r => r.GoodsReceiptId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Note, "forbidden")));
        await Assert.ThrowsAsync<SqlException>(() => db.GoodsReceipts.Where(r => r.GoodsReceiptId == id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<ConflictException>(() => DraftAsync(batch, location));
    }

    private sealed class RetryCommitInterceptor(bool afterCommit) : DbTransactionInterceptor
    {
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && Failures++ == 0) throw new TimeoutException("Test transient failure before commit");
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (afterCommit && Failures++ == 0) throw new TimeoutException("Test unknown successful commit outcome");
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    [ReceivingSqlFact]
    public async Task Execution_strategy_retries_rollback_and_unknown_commit_without_double_inventory()
    {
        foreach (var afterCommit in new[] { false, true })
        {
            var (batch, location) = await EligibleAsync();
            var interceptor = new RetryCommitInterceptor(afterCommit);
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString,
                sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null)).AddInterceptors(interceptor).Options;
            var repo = new GoodsReceiptRepository(options, Accessor);
            var id = await repo.CreateAsync(new(batch.ProductBatchId, location.WarehouseLocationId, Today, null), F.ActorId, default);
            Assert.True(interceptor.Failures >= 2);
            interceptor = new RetryCommitInterceptor(afterCommit);
            options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(F.ConnectionString,
                sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null)).AddInterceptors(interceptor).Options;
            repo = new GoodsReceiptRepository(options, Accessor);
            await ConfirmAsync(repo, id);
            Assert.True(interceptor.Failures >= 2);
            await using var db = F.Context();
            Assert.Single(await db.GoodsReceipts.Where(r => r.ProductBatchId == batch.ProductBatchId).ToListAsync());
            Assert.Equal(1000, (await db.Inventories.SingleAsync(i => i.ProductBatchId == batch.ProductBatchId)).QuantityOnHand);
            Assert.Single(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId && h.NewStatus == "IN_STOCK").ToListAsync());
            Assert.Single(await db.AuditLogs.Where(a => a.EntityName == "GOODS_RECEIPT" && a.ActionType == "UPDATE" && a.EntityId == "GoodsReceiptId=" + id).ToListAsync());
        }
    }

    [ReceivingSqlFact]
    public async Task Receipt_keeps_uc28_locked_and_public_qr_readable_after_stock_commit()
    {
        var (batch, location) = await EligibleAsync(); var id = await DraftAsync(batch, location);
        await using var db = F.Context();
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        db.QrCodes.Add(new QrCode { ProductBatchId = batch.ProductBatchId, PublicToken = token,
            TraceabilityUrl = "https://test.invalid/trace/" + token, QrImageUrl = "https://test.invalid/qr.png", IsActive = true, GeneratedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await ConfirmAsync(Repository(), id);
        var publicResult = await new SAW.Application.Features.Traceability.Services.TraceabilityService(new TraceabilityRepository(db)).GetAsync(token, default);
        Assert.Equal(batch.BatchCode, publicResult.BatchCode);
        Assert.Contains(publicResult.Milestones, m => m.Type == "RECEIVED");
        var result = await new ProductBatchVerificationRepository(db, Accessor).UpdateAsync(batch.ProductBatchId,
            batch.UpdatedAt, batch.CreatedAt, F.ActorId, new(100, 1000, null, null, null, null), default);
        Assert.Equal(SAW.Application.Repositories.ProductBatchReceivingUpdateResult.QcReceived, result);
    }

    [ReceivingSqlFact]
    public async Task Concurrent_drafts_and_confirms_do_not_duplicate_stock()
    {
        var (batch, location) = await EligibleAsync();
        async Task<long?> Create() { try { return await DraftAsync(batch, location); } catch (ConflictException) { return null; } }
        var results = await Task.WhenAll(Create(), Create());
        var id = Assert.Single(results, r => r.HasValue)!.Value;
        await Task.WhenAll(ConfirmAsync(Repository(), id), ConfirmAsync(Repository(), id));
        await using var db = F.Context();
        Assert.Single(await db.GoodsReceipts.Where(r => r.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Equal(1000, (await db.Inventories.SingleAsync(i => i.ProductBatchId == batch.ProductBatchId)).QuantityOnHand);
        Assert.Single(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId && h.NewStatus == "IN_STOCK").ToListAsync());
    }

    [ReceivingSqlFact]
    public async Task Competing_batches_cannot_overfill_location_or_warehouse()
    {
        foreach (var wholeWarehouse in new[] { false, true })
        {
            var (a, location) = await EligibleAsync(1000, wholeWarehouse ? null : 1500);
            var (b, secondLocation) = await EligibleAsync();
            await using var db = F.Context();
            if (wholeWarehouse)
            {
                var current = await db.Inventories.SumAsync(i => (decimal?)i.QuantityOnHand) ?? 0;
                db.WarehouseSettings.Add(new WarehouseSetting { WarehouseSettingId = 1, WarehouseName = "Test", MaxCapacityKg = current + 1500 });
                await db.SaveChangesAsync();
            }
            try
            {
                var first = await DraftAsync(a, location); var second = await DraftAsync(b, wholeWarehouse ? secondLocation : location);
                async Task<bool> Commit(long id) { try { await ConfirmAsync(Repository(), id); return true; } catch (ConflictException) { return false; } }
                var results = await Task.WhenAll(Commit(first), Commit(second));
                Assert.Single(results, r => r);
                Assert.Equal(1000, await db.Inventories.Where(i => i.ProductBatchId == a.ProductBatchId || i.ProductBatchId == b.ProductBatchId).SumAsync(i => i.QuantityOnHand));
            }
            finally { if (wholeWarehouse) await db.WarehouseSettings.ExecuteDeleteAsync(); }
        }
    }

    [ReceivingSqlFact]
    public async Task Latest_qc_missing_verified_inactive_location_and_status_are_rechecked_at_write()
    {
        var (batch, location) = await EligibleAsync();
        var id = await DraftAsync(batch, location);
        await using var db = F.Context();
        var auditCount = await db.AuditLogs.CountAsync();
        db.QcInspections.Add(F.Inspection(batch.ProductBatchId)); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => ConfirmAsync(Repository(), id));
        Assert.Empty(await db.Inventories.Where(i => i.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Equal("DRAFT", (await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.GoodsReceiptId == id)).ReceiptStatus);
        Assert.Equal(auditCount + 1, await db.AuditLogs.CountAsync()); // Only the fixture QC insertion.
        foreach (var state in new[] { "SUBMITTED", "PENDING_QC", "QUARANTINE", "REJECTED", "CANCELLED" })
        {
            var (invalid, loc) = await EligibleAsync();
            await db.ProductBatches.Where(b => b.ProductBatchId == invalid.ProductBatchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.BatchStatus, state));
            await Assert.ThrowsAsync<ConflictException>(() => DraftAsync(invalid, loc));
        }
        var (missing, active) = await EligibleAsync();
        await db.ProductBatches.Where(b => b.ProductBatchId == missing.ProductBatchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.VerifiedWeightInKg, (decimal?)null));
        await Assert.ThrowsAsync<ConflictException>(() => DraftAsync(missing, active));
        var (valid, inactive) = await EligibleAsync();
        await db.WarehouseLocations.Where(l => l.WarehouseLocationId == inactive.WarehouseLocationId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LocationStatus, "INACTIVE"));
        await Assert.ThrowsAsync<ConflictException>(() => DraftAsync(valid, inactive));
    }

    [ReceivingSqlFact]
    public async Task Changed_snapshot_and_two_legacy_drafts_cannot_receive_twice()
    {
        var (batch, location) = await EligibleAsync(); var id = await DraftAsync(batch, location);
        await using var db = F.Context();
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.VerifiedQuantity, 101m));
        await Assert.ThrowsAsync<ConflictException>(() => ConfirmAsync(Repository(), id));
        Assert.Empty(await db.Inventories.Where(i => i.ProductBatchId == batch.ProductBatchId).ToListAsync());
        var (legacy, loc) = await EligibleAsync(); var first = await DraftAsync(legacy, loc);
        var duplicate = new GoodsReceipt { ProductBatchId = legacy.ProductBatchId, WarehouseLocationId = loc.WarehouseLocationId,
            OperationAccountId = F.ActorId, ReceiptCode = Guid.NewGuid().ToString(), Unit = "Bao", ReceivedQuantity = 100, WeightInKg = 1000, ReceivedAt = DateTime.UtcNow };
        db.Add(duplicate); await db.SaveChangesAsync();
        async Task<bool> Commit(long receiptId) { try { await ConfirmAsync(Repository(), receiptId); return true; } catch (ConflictException) { return false; } }
        Assert.Single(await Task.WhenAll(Commit(first), Commit(duplicate.GoodsReceiptId)), x => x);
        Assert.Equal(1000, await db.Inventories.Where(i => i.ProductBatchId == legacy.ProductBatchId).SumAsync(i => i.QuantityOnHand));
    }

    [ReceivingSqlFact]
    public async Task Failure_during_stock_write_rolls_back_receipt_batch_history_and_audit()
    {
        var (batch, location) = await EligibleAsync(); var id = await DraftAsync(batch, location);
        await using var db = F.Context();
        var audits = await db.AuditLogs.CountAsync();
        var history = await db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == batch.ProductBatchId);
        // Test-only constraint guarantees a real SQL failure during the stock write.
        var failureDdl = "ALTER TABLE INVENTORY ADD CONSTRAINT CK_RECEIPT_TEST_FAILURE CHECK (ProductBatchID <> "
            + batch.ProductBatchId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        await db.Database.ExecuteSqlRawAsync(failureDdl);
        try
        {
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => ConfirmAsync(Repository(), id));
            Assert.Equal(547, Assert.IsType<SqlException>(failure.InnerException).Number);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE INVENTORY DROP CONSTRAINT CK_RECEIPT_TEST_FAILURE"); }
        Assert.Equal(audits, await db.AuditLogs.CountAsync());
        Assert.Equal(history, await db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == batch.ProductBatchId));
        Assert.Equal("DRAFT", (await db.GoodsReceipts.SingleAsync(r => r.GoodsReceiptId == id)).ReceiptStatus);
        Assert.Equal("APPROVED_FOR_STORAGE", (await db.ProductBatches.SingleAsync(b => b.ProductBatchId == batch.ProductBatchId)).BatchStatus);
        Assert.Empty(await db.Inventories.Where(i => i.ProductBatchId == batch.ProductBatchId).ToListAsync());
    }

    [ReceivingSqlFact]
    public async Task List_filters_sort_paging_and_api_authorization_and_untrusted_fields()
    {
        var (batch, location) = await EligibleAsync();
        using var client = await F.ApiAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/operation/goods-receipts")).StatusCode);
        string Token(string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test", "uc28-test",
            [new Claim("account_id", F.ActorId.ToString()), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256)));
        foreach (var role in new[] { "SUPPLIER", "QC_STAFF", "ADMINISTRATOR" })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operation/goods-receipts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/operation/goods-receipts", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/operation/goods-receipts/1/draft", new { })).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("OPERATION_STAFF"));
        var created = await client.PostAsJsonAsync("/api/operation/goods-receipts", new { productBatchId = batch.ProductBatchId,
            warehouseLocationId = location.WarehouseLocationId, receivedDate = Today, note = " test ", receivedQuantity = 1,
            weightInKg = 1, operationAccountId = -1, supplierId = -1, receiptStatus = "COMMITTED" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await using var db = F.Context();
        var receipt = await db.GoodsReceipts.SingleAsync(r => r.ProductBatchId == batch.ProductBatchId);
        Assert.Equal(F.ActorId, receipt.OperationAccountId); Assert.Equal(1000, receipt.WeightInKg); Assert.Equal("DRAFT", receipt.ReceiptStatus); Assert.Equal("test", receipt.Note);
        var service = Service(db);
        var found = await service.SearchAsync(new(Search: batch.BatchCode, SupplierId: F.SupplierId, WarehouseLocationId: location.WarehouseLocationId, FromDate: Today, ToDate: Today, Status: "DRAFT", PageSize: 1), default);
        Assert.Equal(1, found.TotalCount); Assert.Equal(receipt.GoodsReceiptId, Assert.Single(found.Items).Id);
        var query = new GoodsReceiptQuery(PageSize: 2, SortBy: "receivedAtDesc");
        var page1 = await service.SearchAsync(query, default); var page2 = await service.SearchAsync(query with { Page = 2 }, default);
        Assert.Empty(page1.Items.Select(r => r.Id).Intersect(page2.Items.Select(r => r.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operation/goods-receipts?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operation/goods-receipts?fromDate=2026-02-02&toDate=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/operation/goods-receipts/9223372036854775807")).StatusCode);
        var snapshot = new GoodsReceiptSnapshot(receipt.WarehouseLocationId, receipt.ReceivedAt, receipt.Note);
        var editPath = $"/api/operation/goods-receipts/{receipt.GoodsReceiptId}/draft";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(editPath, new {
            warehouseLocationId = location.WarehouseLocationId, receivedDate = Today, note = "tampered",
            expectedSnapshot = snapshot, receivedQuantity = 999, productBatchId = -1, receiptStatus = "COMMITTED"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(editPath,
            new UpdateGoodsReceiptDraftRequest(location.WarehouseLocationId, Today.AddDays(1), null, snapshot))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(editPath,
            new UpdateGoodsReceiptDraftRequest(location.WarehouseLocationId, Today, new string('x', 1001), snapshot))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/operation/goods-receipts/{receipt.GoodsReceiptId}/confirm", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(editPath,
            new UpdateGoodsReceiptDraftRequest(location.WarehouseLocationId, Today, null, snapshot with { Note = "stale" }))).StatusCode);
        await db.WarehouseLocations.Where(l => l.WarehouseLocationId == location.WarehouseLocationId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LocationStatus, "INACTIVE"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(editPath,
            new UpdateGoodsReceiptDraftRequest(location.WarehouseLocationId, Today, null, snapshot))).StatusCode);
        await db.WarehouseLocations.Where(l => l.WarehouseLocationId == location.WarehouseLocationId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LocationStatus, "ACTIVE"));
        var confirm = await client.PutAsJsonAsync($"/api/operation/goods-receipts/{receipt.GoodsReceiptId}/confirm", new ConfirmGoodsReceiptRequest(new(receipt.WarehouseLocationId, receipt.ReceivedAt, receipt.Note)));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(editPath,
            new UpdateGoodsReceiptDraftRequest(location.WarehouseLocationId, Today, null, snapshot))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/operation/goods-receipts/{receipt.GoodsReceiptId}/confirm", new ConfirmGoodsReceiptRequest(new(receipt.WarehouseLocationId, receipt.ReceivedAt, receipt.Note)))).StatusCode);
        var committed = await service.SearchAsync(new(Search: batch.BatchCode, Status: "COMMITTED"), default);
        Assert.Single(committed.Items);
    }
}
