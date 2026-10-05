using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Exceptions;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories.Suppliers;
using SAW.Infrastructure.Repositories;
using SAW.Application.Features.ProductBatches.Dtos;

namespace SAW.Test.Application.Suppliers;

public sealed class SupplierSqlFactAttribute : FactAttribute
{
    public SupplierSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SAW_SUPPLIER_TEST_SQL")))
            Skip = "Set SAW_SUPPLIER_TEST_SQL to a dedicated database ending in _SupplierTests.";
    }
}

public class SupplierSqlIntegrationTests : IAsyncLifetime
{
    private DbContextOptions<AppDbContext> _options = null!;
    private AppDbContext _db = null!;
    private int _accountId, _supplierId, _cropId, _areaId;
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "saw-supplier-files-" + Guid.NewGuid().ToString("N"));
    private ProductBatchRepository Repository(AppDbContext? db = null) => new(db ?? _db);
    private SupplierBatchCommandService Service(AppDbContext? db = null) => new(Repository(db), new SupplierRepository(db ?? _db));
    private sealed class FixedTime(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("SAW_SUPPLIER_TEST_SQL");
        if (connection is null) return;
        var builder = new SqlConnectionStringBuilder(connection);
        if (!builder.InitialCatalog.EndsWith("_SupplierTests", StringComparison.Ordinal))
            throw new InvalidOperationException("Tests require an isolated database ending in _SupplierTests.");
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        _db = new AppDbContext(_options);
        var creator = _db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync()) await creator.CreateAsync();
        if (!await creator.HasTablesAsync())
        {
            // The shared model has multiple cascade paths outside Supplier. Test only Supplier
            // operations against its real mappings; delete cascades are not under test here.
            var script = _db.Database.GenerateCreateScript().Replace("ON DELETE CASCADE", "ON DELETE NO ACTION");
            foreach (var batch in Regex.Split(script, @"^GO\s*$", RegexOptions.Multiline))
                if (!string.IsNullOrWhiteSpace(batch)) await _db.Database.ExecuteSqlRawAsync(batch);
        }
        var key = Guid.NewGuid().ToString("N");
        var role = new Role { RoleCode = key[..20], RoleName = "Supplier test", IsActive = true };
        var account = new Account { Role = role, Username = key, Email = key + "@test.local", PasswordHash = "test-only", FullName = "Supplier test", AccountStatus = "ACTIVE" };
        var supplier = new Supplier { Account = account, SupplierCode = key[..25], TaxCode = key, SupplierName = "Supplier test", ContactPerson = "Contact", Address = "Address", ProfileStatus = "ACTIVE" };
        var crop = new CropType { CropCode = key[..25], CropName = key, CategoryName = "Fruit", IsActive = true };
        var area = new GrowingArea { AreaName = "Area B", Region = "South", Province = "Same province", District = "Same district", Ward = "Ward" };
        _db.AddRange(supplier, crop, area);
        await _db.SaveChangesAsync();
        _accountId = account.AccountId; _supplierId = supplier.SupplierId; _cropId = crop.CropTypeId; _areaId = area.GrowingAreaId;
        _db.SupplierCropTypes.Add(new() { SupplierId = _supplierId, CropTypeId = _cropId, IsActive = true });
        _db.SupplierGrowingAreas.Add(new() { SupplierId = _supplierId, GrowingAreaId = _areaId });
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS dbo.TR_SUPPLIER_TEST_HISTORY");
        _db.ChangeTracker.Clear();
    }

    public Task DisposeAsync()
    {
        _db?.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, true);
        return Task.CompletedTask;
    }

    private DeclareProductBatchRequest Declaration(List<string>? documents = null) => new()
    {
        CropTypeId = _cropId, GrowingAreaId = _areaId, ProductName = "Cam", HarvestDate = new(2026, 1, 1),
        Unit = "Thùng", DeclaredQuantity = 10, PackageCount = 10, PackageUnitWeightKg = 10, EvidenceDocumentUrls = documents
    };
    private UpdateProductBatchRequest Update(SupplierBatchStatusResponse detail, List<string>? documents = null) => new()
    {
        CropTypeId = _cropId, GrowingAreaId = _areaId, ProductName = "Cam mới", HarvestDate = new(2026, 1, 1),
        Unit = "Kg", DeclaredQuantity = 250, ExpectedCreatedAt = detail.CreatedAt,
        ExpectedUpdatedAt = detail.UpdatedAt, EvidenceDocumentUrls = documents
    };

    private async Task<string> Upload(bool avatar = false)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(_storage);
        var controller = new SupplierFilesController(_db, new ConfigurationBuilder().Build(), environment.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _accountId.ToString())], "test")) } }
        };
        var bytes = avatar ? new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 } : Encoding.ASCII.GetBytes("%PDF-1.4\ntest fixture");
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", avatar ? "avatar.png" : "A.pdf");
        var response = Assert.IsType<OkObjectResult>(await controller.Upload(file, avatar ? "AVATAR" : "DOCUMENT"));
        return JsonSerializer.SerializeToElement(response.Value).GetProperty("url").GetString()!;
    }

    [SupplierSqlFact]
    public async Task CreateEditCancelPreserveWeightVersionAndSingleHistoryWithoutTrigger()
    {
        var item = await Service().DeclareBatchAsync(_accountId, Declaration());
        Assert.Equal(0.1m, item.QuantityInTons);
        var detail = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        Assert.Equal(_areaId, detail.GrowingAreaId); Assert.Equal(100m, detail.WeightInKg);
        await Service().UpdateDeclaredBatchAsync(item.BatchId, _accountId, Update(detail));
        var updated = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        Assert.Equal(250m, updated.WeightInKg); Assert.NotNull(updated.UpdatedAt);
        Assert.Single(updated.StatusHistory);
        await Assert.ThrowsAsync<ConflictException>(() => Service().UpdateDeclaredBatchAsync(item.BatchId, _accountId, Update(detail)));
        await Service().CancelBatchAsync(item.BatchId, _accountId, new() { ExpectedCreatedAt = updated.CreatedAt, ExpectedUpdatedAt = updated.UpdatedAt });
        var canceled = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        Assert.Equal("CANCELLED", canceled.CurrentStatus); Assert.Equal(2, canceled.StatusHistory.Count);
        Assert.All(await _db.BatchStatusHistories.Where(h => h.ProductBatchId == item.BatchId).ToListAsync(), h => Assert.Equal(_accountId, h.ChangedByAccountId));
    }

    [SupplierSqlFact]
    public async Task TriggerDoesNotDuplicateCreateOrCancelHistory()
    {
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER dbo.TR_SUPPLIER_TEST_HISTORY ON dbo.PRODUCT_BATCH AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                INSERT dbo.BATCH_STATUS_HISTORY (ProductBatchID, OldStatus, NewStatus, ChangedByAccountID, ChangedAt)
                SELECT i.ProductBatchID, d.BatchStatus, i.BatchStatus, TRY_CONVERT(int, SESSION_CONTEXT(N'AccountID')), SYSDATETIME()
                FROM inserted i LEFT JOIN deleted d ON d.ProductBatchID = i.ProductBatchID
                WHERE d.ProductBatchID IS NULL OR d.BatchStatus <> i.BatchStatus;
            END
            """);
        var item = await Service().DeclareBatchAsync(_accountId, Declaration());
        var detail = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        await Service().CancelBatchAsync(item.BatchId, _accountId, new() { ExpectedCreatedAt = detail.CreatedAt, ExpectedUpdatedAt = detail.UpdatedAt });
        Assert.Equal(2, await _db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == item.BatchId));
        Assert.All(await _db.BatchStatusHistories.Where(h => h.ProductBatchId == item.BatchId).ToListAsync(), h => Assert.Equal(_accountId, h.ChangedByAccountId));
    }

    [SupplierSqlFact]
    public async Task StaffTransitionDuringSupplierRequestCannotBeOverwritten()
    {
        var item = await Service().DeclareBatchAsync(_accountId, Declaration());
        var stale = await Repository().GetBatchByIdAsync(item.BatchId);
        await using var staffDb = new AppDbContext(_options);
        await using var tx = await staffDb.Database.BeginTransactionAsync();
        await staffDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus=N'PENDING_QC', VerifiedQuantity=9, VerifiedWeightInKg=90 WHERE ProductBatchID={item.BatchId}");
        await using var supplierDb = new AppDbContext(_options);
        var write = Repository(supplierDb).UpdateProductBatchAsync(stale!, new()
        { ProductBatchId = item.BatchId, OldStatus = "SUBMITTED", NewStatus = "SUBMITTED", ChangedByAccountId = _accountId },
            stale!.CreatedAt, stale.UpdatedAt, null);
        await Task.Delay(100);
        Assert.False(write.IsCompleted);
        await tx.CommitAsync();
        await Assert.ThrowsAsync<ConflictException>(() => write);
        var current = await Repository(supplierDb).GetBatchByIdAsync(item.BatchId);
        Assert.Equal("PENDING_QC", current!.BatchStatus); Assert.Equal(90m, current.VerifiedWeightInKg);
    }

    [SupplierSqlFact]
    public async Task SupplierCancelCannotOverwriteConcurrentConfirmOrReject()
    {
        foreach (var status in new[] { "PENDING_QC", "REJECTED" })
        {
            var item = await Service().DeclareBatchAsync(_accountId, Declaration());
            var stale = (await Repository().GetBatchByIdAsync(item.BatchId))!;
            var created = stale.CreatedAt; var updated = stale.UpdatedAt;
            stale.BatchStatus = "CANCELLED";
            await using var staffDb = new AppDbContext(_options);
            await using var tx = await staffDb.Database.BeginTransactionAsync();
            await staffDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus={status}, VerifiedQuantity=9, VerifiedWeightInKg=90 WHERE ProductBatchID={item.BatchId}");
            await using var supplierDb = new AppDbContext(_options);
            var cancel = Repository(supplierDb).UpdateProductBatchAsync(stale, new()
            { ProductBatchId = item.BatchId, OldStatus = "SUBMITTED", NewStatus = "CANCELLED", ChangedByAccountId = _accountId }, created, updated, null);
            await Task.Delay(100);
            Assert.False(cancel.IsCompleted);
            await tx.CommitAsync();
            await Assert.ThrowsAsync<ConflictException>(() => cancel);
            var current = await Repository(supplierDb).GetBatchByIdAsync(item.BatchId);
            Assert.Equal(status, current!.BatchStatus); Assert.Equal(90m, current.VerifiedWeightInKg);
        }
    }

    [SupplierSqlFact]
    public async Task ExistingStaffRepositoryRejectsReceivingAfterSupplierCancel()
    {
        var item = await Service().DeclareBatchAsync(_accountId, Declaration());
        var detail = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        await Service().CancelBatchAsync(item.BatchId, _accountId, new() { ExpectedCreatedAt = detail.CreatedAt, ExpectedUpdatedAt = detail.UpdatedAt });
        await using var staffDb = new AppDbContext(_options);
        var staff = new ProductBatchVerificationRepository(staffDb, new HttpContextAccessor());
        var history = new BatchStatusHistory { ProductBatchId = item.BatchId, OldStatus = "SUBMITTED", NewStatus = "PENDING_QC", ChangedByAccountId = _accountId, ChangedAt = DateTime.UtcNow };
        Assert.False(await staff.ConfirmAsync(item.BatchId, _supplierId, new VerifiedReceivingDetails(9, 90, null, null, null, "Receiving"), history, default));
        history.NewStatus = "REJECTED";
        Assert.False(await staff.RejectAsync(item.BatchId, _supplierId, "Reject", history, default));
        Assert.Equal("CANCELLED", (await Repository(staffDb).GetBatchByIdAsync(item.BatchId))!.BatchStatus);
        Assert.Equal(2, await staffDb.BatchStatusHistories.CountAsync(h => h.ProductBatchId == item.BatchId));
    }

    [SupplierSqlFact]
    public async Task FilteredSummaryAndListUseRealStatusesAndSameWeightAsDetail()
    {
        var first = await Service().DeclareBatchAsync(_accountId, Declaration());
        var secondRequest = Declaration(); secondRequest.Unit = "Bao"; secondRequest.DeclaredQuantity = 20;
        secondRequest.PackageCount = 20; secondRequest.PackageUnitWeightKg = 25;
        var second = await Service().DeclareBatchAsync(_accountId, secondRequest);
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus=N'APPROVED_FOR_STORAGE' WHERE ProductBatchID={second.BatchId}");
        var all = await Service().GetDeclaredBatchesAsync(_accountId, new());
        Assert.Equal(2, all.Summary.TotalDeclaredBatches); Assert.Equal(1, all.Summary.ApprovedBatches);
        Assert.Equal(.1m, all.Batches.Items.Single(b => b.BatchId == first.BatchId).QuantityInTons);
        Assert.Equal(.5m, all.Batches.Items.Single(b => b.BatchId == second.BatchId).QuantityInTons);
        Assert.Equal(500m, (await Service().GetBatchStatusDetailAsync(second.BatchId, _accountId)).WeightInKg);
        var filtered = await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "APPROVED_FOR_STORAGE" });
        Assert.Single(filtered.Batches.Items); Assert.Equal(1, filtered.Summary.TotalDeclaredBatches);
        Assert.Equal(1, filtered.Summary.ApprovedBatches); Assert.Equal(0, filtered.Summary.PendingApprovalBatches);
    }

    [SupplierSqlFact]
    public async Task GrowingAreaFilterDistinguishesAreasInSameProvinceBeforePaging()
    {
        var otherArea = new GrowingArea { AreaName = "Area C", Region = "South", Province = "Same province", District = "Same district", Ward = "Other ward" };
        _db.GrowingAreas.Add(otherArea);
        await _db.SaveChangesAsync();
        _db.SupplierGrowingAreas.Add(new() { SupplierId = _supplierId, GrowingAreaId = otherArea.GrowingAreaId });
        await _db.SaveChangesAsync();
        var selectedIds = new List<long>();
        for (var i = 0; i < 3; i++) selectedIds.Add((await Service().DeclareBatchAsync(_accountId, Declaration())).BatchId);
        var other = Declaration();
        other.GrowingAreaId = otherArea.GrowingAreaId;
        await Service().DeclareBatchAsync(_accountId, other);

        var firstPage = await Service().GetDeclaredBatchesAsync(_accountId, new() { GrowingAreaId = _areaId, PageSize = 2 });
        var secondPage = await Service().GetDeclaredBatchesAsync(_accountId, new() { GrowingAreaId = _areaId, PageSize = 2, PageIndex = 2 });
        Assert.Equal(3, firstPage.Batches.TotalCount);
        Assert.Equal(3, firstPage.Summary.TotalDeclaredBatches);
        Assert.Equal(2, firstPage.Batches.TotalPages);
        Assert.Equal(2, firstPage.Batches.Items.Count);
        Assert.Single(secondPage.Batches.Items);
        Assert.Equal(selectedIds.Order(), firstPage.Batches.Items.Concat(secondPage.Batches.Items).Select(b => b.BatchId).Order());
    }

    [SupplierSqlFact]
    public async Task StoredFilterKeepsLaterWarehouseStatusesAtSupplierFinalMilestone()
    {
        var storedIds = new List<long>();
        foreach (var status in new[] { "IN_STOCK", "RESERVED", "PARTIALLY_ISSUED", "ISSUED" })
        {
            var item = await Service().DeclareBatchAsync(_accountId, Declaration());
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus={status} WHERE ProductBatchID={item.BatchId}");
            storedIds.Add(item.BatchId);
        }
        await Service().DeclareBatchAsync(_accountId, Declaration());
        var response = await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "IN_STOCK" });
        Assert.Equal(4, response.Batches.TotalCount);
        Assert.Equal(storedIds.Order(), response.Batches.Items.Select(b => b.BatchId).Order());
        Assert.All(response.Batches.Items, b => { Assert.Equal("IN_STOCK", b.Status); Assert.Equal("Đã nhập kho", b.StatusDisplayName); });
        Assert.Single((await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "SUBMITTED" })).Batches.Items);
    }

    [SupplierSqlFact]
    public async Task ConcurrentDeclarationsReceiveUniquePersistedShortCodes()
    {
        var clock = new FixedTime(new DateTimeOffset(2060, 1, 1, 17, 0, 0, TimeSpan.Zero));
        var codes = await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            await using var context = new AppDbContext(_options);
            var service = new SupplierBatchCommandService(new ProductBatchRepository(context, clock), new SupplierRepository(context));
            return (await service.DeclareBatchAsync(_accountId, Declaration())).BatchCode;
        }));
        Assert.Equal(24, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches(@"^LH-20600102-\d{4}$", code));
        Assert.Equal(24, await _db.ProductBatches.CountAsync(batch => batch.SupplierId == _supplierId));
        var savedCounter = await _db.ProductBatchDailyCounters.SingleAsync(counter => counter.CodeDate == new DateOnly(2060, 1, 2));
        Assert.Equal(codes.Max(code => int.Parse(code.Split('-')[2])), savedCounter.LastNumber);
        await using var reopened = new AppDbContext(_options);
        var next = await new ProductBatchRepository(reopened, clock).GenerateBatchCodeAsync();
        Assert.Equal(savedCounter.LastNumber + 1, int.Parse(next.Split('-')[2]));
    }

    [SupplierSqlFact]
    public async Task DailyCodeCounterChangesDateAtVietnamMidnightAndKeepsOldCodes()
    {
        var before = new ProductBatchRepository(_db, new FixedTime(new DateTimeOffset(2061, 1, 1, 16, 59, 59, TimeSpan.Zero)));
        var after = new ProductBatchRepository(_db, new FixedTime(new DateTimeOffset(2061, 1, 1, 17, 0, 0, TimeSpan.Zero)));
        Assert.Equal("LH-20610101-0001", await before.GenerateBatchCodeAsync());
        Assert.Equal("LH-20610101-0002", await before.GenerateBatchCodeAsync());
        Assert.Equal("LH-20610102-0001", await after.GenerateBatchCodeAsync());
        var legacy = await Service().DeclareBatchAsync(_accountId, Declaration());
        const string legacyCode = "LH-20261005-17da0453a0e84f549d24a98928f603fd";
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchCode={legacyCode} WHERE ProductBatchID={legacy.BatchId}");
        Assert.Equal(legacyCode, (await Service().GetBatchStatusDetailAsync(legacy.BatchId, _accountId)).BatchCode);
        Assert.Equal(legacy.BatchId, Assert.Single((await Service().GetDeclaredBatchesAsync(_accountId, new() { Keyword = legacyCode })).Batches.Items).BatchId);
    }

    [SupplierSqlFact]
    public async Task SupplierListSummaryFilterAndDetailAgreeAfterEntryAndLaterInspection()
    {
        foreach (var status in new[] { "QUARANTINE", "REJECTED", "PENDING_QC", "ISSUED" })
        {
            var item = await Service().DeclareBatchAsync(_accountId, Declaration());
            _db.BatchStatusHistories.Add(new() { ProductBatchId = item.BatchId, OldStatus = "APPROVED_FOR_STORAGE", NewStatus = "IN_STOCK",
                ChangedAt = DateTime.UtcNow, ChangedByAccountId = _accountId });
            await _db.SaveChangesAsync();
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus={status} WHERE ProductBatchID={item.BatchId}");
            Assert.Equal("IN_STOCK", (await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId)).CurrentStatus);
            Assert.Equal(status, (await Repository().GetBatchByIdAsync(item.BatchId))!.BatchStatus);
        }
        var preStorage = await Service().DeclareBatchAsync(_accountId, Declaration());
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus=N'QUARANTINE' WHERE ProductBatchID={preStorage.BatchId}");
        var stored = await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "IN_STOCK", PageSize = 2 });
        Assert.Equal(4, stored.Batches.TotalCount); Assert.Equal(2, stored.Batches.TotalPages);
        Assert.Equal(4, stored.Summary.ApprovedBatches);
        Assert.Equal(0, stored.Summary.PendingQCBatches); Assert.Equal(0, stored.Summary.RejectedBatches);
        Assert.All(stored.Batches.Items, item => Assert.Equal("IN_STOCK", item.Status));
        var quarantined = await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "QUARANTINE" });
        Assert.Equal(preStorage.BatchId, Assert.Single(quarantined.Batches.Items).BatchId);
        Assert.Equal("QUARANTINE", (await Service().GetBatchStatusDetailAsync(preStorage.BatchId, _accountId)).CurrentStatus);
        Assert.Equal(0, quarantined.Summary.ApprovedBatches);
        Assert.Empty((await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "REJECTED" })).Batches.Items);
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "ISSUED" }));
    }

    [SupplierSqlFact]
    public async Task DraftReceiptDoesNotFinishSupplierProgressButCommittedReceiptDoes()
    {
        var item = await Service().DeclareBatchAsync(_accountId, Declaration());
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus=N'APPROVED_FOR_STORAGE' WHERE ProductBatchID={item.BatchId}");
        var location = new WarehouseLocation { LocationCode = Guid.NewGuid().ToString("N"), ZoneName = "Supplier test zone" };
        _db.WarehouseLocations.Add(location);
        await _db.SaveChangesAsync();
        var receipt = new GoodsReceipt { ReceiptCode = Guid.NewGuid().ToString("N"), ProductBatchId = item.BatchId,
            WarehouseLocationId = location.WarehouseLocationId, OperationAccountId = _accountId,
            ReceivedQuantity = 10, Unit = "Thùng", WeightInKg = 100, ReceiptStatus = "DRAFT" };
        _db.GoodsReceipts.Add(receipt);
        await _db.SaveChangesAsync();
        var draftDetail = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        Assert.Equal("APPROVED_FOR_STORAGE", draftDetail.CurrentStatus);
        Assert.Null(draftDetail.WarehousedAt); Assert.Equal(0, draftDetail.ReceivedQuantity);
        Assert.Empty((await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "IN_STOCK" })).Batches.Items);
        Assert.Single((await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "APPROVED_FOR_STORAGE" })).Batches.Items);

        receipt.ReceiptStatus = "COMMITTED"; receipt.CommittedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PRODUCT_BATCH SET BatchStatus=N'QUARANTINE' WHERE ProductBatchID={item.BatchId}");
        var committedDetail = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        Assert.Equal("IN_STOCK", committedDetail.CurrentStatus);
        Assert.NotNull(committedDetail.WarehousedAt); Assert.Equal(10, committedDetail.ReceivedQuantity);
        var stored = await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "IN_STOCK" });
        Assert.Single(stored.Batches.Items); Assert.Equal(1, stored.Summary.ApprovedBatches);
        Assert.Empty((await Service().GetDeclaredBatchesAsync(_accountId, new() { Status = "QUARANTINE" })).Batches.Items);
    }

    [SupplierSqlFact]
    public async Task AttachReplaceRemoveFilesPersistAndFailedSaveKeepsOldFile()
    {
        var a = await Upload();
        var item = await Service().DeclareBatchAsync(_accountId, Declaration([a]));
        var detail = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        Assert.Equal(a, Assert.Single(detail.Documents).FileUrl);
        await using (var rejectedDb = new AppDbContext(_options))
            await Assert.ThrowsAsync<ArgumentException>(() => Service(rejectedDb).UpdateDeclaredBatchAsync(item.BatchId, _accountId,
                Update(detail, ["/api/supplier-files/" + Guid.NewGuid()])));
        Assert.Equal(a, Assert.Single((await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId)).Documents).FileUrl);
        var b = await Upload();
        await Service().UpdateDeclaredBatchAsync(item.BatchId, _accountId, Update(detail, [b]));
        var replaced = await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId);
        Assert.Equal(b, Assert.Single(replaced.Documents).FileUrl);
        await Service().UpdateDeclaredBatchAsync(item.BatchId, _accountId, Update(replaced, []));
        Assert.Empty((await Service().GetBatchStatusDetailAsync(item.BatchId, _accountId)).Documents);
    }

    [SupplierSqlFact]
    public async Task UnregisteredAreaAndForeignUploadAreRejectedWithoutCreatingBatch()
    {
        var area = new GrowingArea { AreaName = "Other area", Region = "South", Province = "Same province", District = "Same district", Ward = "Ward" };
        _db.GrowingAreas.Add(area); await _db.SaveChangesAsync();
        var request = Declaration(); request.GrowingAreaId = area.GrowingAreaId;
        await Assert.ThrowsAsync<ArgumentException>(() => Service().DeclareBatchAsync(_accountId, request));
        Assert.False(await _db.ProductBatches.AnyAsync(b => b.SupplierId == _supplierId));
        var key = Guid.NewGuid().ToString("N");
        var other = new Account { RoleId = (await _db.Accounts.FindAsync(_accountId))!.RoleId,
            Username = key, Email = key + "@test.local", PasswordHash = "test-only", FullName = "Other", AccountStatus = "ACTIVE" };
        _db.Accounts.Add(other); await _db.SaveChangesAsync();
        var uploaded = await Upload();
        var file = await _db.Set<SupplierFile>().SingleAsync(f => f.AccountId == _accountId);
        file.AccountId = other.AccountId; await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Service().DeclareBatchAsync(_accountId, Declaration([uploaded])));
        await using var verifyDb = new AppDbContext(_options);
        Assert.False(await verifyDb.ProductBatches.AnyAsync(b => b.SupplierId == _supplierId));
    }

    [SupplierSqlFact]
    public async Task SupplierProfileSavesDocumentsAndAvatarAndCanBeUpdatedTwice()
    {
        var avatar = await Upload(true); var document = await Upload();
        var repository = new SupplierRepository(_db);
        var service = new SupplierCommandService(repository);
        var profile = await repository.GetProfileByAccountIdAsync(_accountId);
        var request = new UpdateSupplierProfileRequest
        {
            SupplierName = profile!.SupplierName, TaxCode = profile.TaxCode, ContactPerson = "Contact", LegalRepresentative = "Contact",
            Address = "Address", CropTypeIds = [_cropId], GrowingAreas = [new() { GrowingAreaId = _areaId }],
            LogoUrl = avatar, EvidenceDocumentUrls = [document]
        };
        await service.UpdateProfileAsync(_accountId, request);
        var saved = await repository.GetProfileByAccountIdAsync(_accountId);
        Assert.Equal(avatar, saved!.LogoUrl); Assert.Equal(document, Assert.Single(saved.Documents).FileUrl);
        request.LogoUrl = await Upload(true); request.EvidenceDocumentUrls = [];
        await service.UpdateProfileAsync(_accountId, request);
        var updated = await repository.GetProfileByAccountIdAsync(_accountId);
        Assert.Equal(request.LogoUrl, updated!.LogoUrl); Assert.Empty(updated.Documents);
    }
}
