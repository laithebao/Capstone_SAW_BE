using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories.Suppliers;

public class ProductBatchRepository(AppDbContext db, TimeProvider? timeProvider = null) : IProductBatchRepository
{
    public async Task<SupplierBatchListResponse> GetBatchesBySupplierAccountIdAsync(int accountId, GetSupplierBatchesQueryRequest r, CancellationToken ct = default)
    {
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.AccountId == accountId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy hồ sơ nhà cung cấp.");
        if (supplier.ProfileStatus != "ACTIVE") throw new UnauthorizedAccessException("Hồ sơ nhà cung cấp chưa hoạt động.");
        if (r.PageIndex < 1 || r.PageSize is < 1 or > 100 || r.FromDate > r.ToDate)
            throw new ArgumentException("Phân trang hoặc khoảng ngày không hợp lệ.");
        var query = db.ProductBatches.AsNoTracking().Where(b => b.SupplierId == supplier.SupplierId)
            .Select(SupplierBatchPresentation.Projection);
        if (!string.IsNullOrWhiteSpace(r.Keyword))
        {
            var kw = r.Keyword.Trim();
            query = query.Where(b => b.Batch.BatchCode.Contains(kw) || b.Batch.ProductName.Contains(kw));
        }
        if (!string.IsNullOrWhiteSpace(r.Status))
        {
            var status = r.Status.Trim().ToUpperInvariant();
            if (!SupplierBatchPresentation.VisibleStatuses.Contains(status)) throw new ArgumentException("Trạng thái lô hàng không hợp lệ.");
            query = query.Where(b => b.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(r.Province)) query = query.Where(b => b.Batch.GrowingArea.Province == r.Province.Trim());
        if (r.GrowingAreaId.HasValue)
        {
            if (r.GrowingAreaId.Value <= 0) throw new ArgumentException("Vùng trồng không hợp lệ.");
            query = query.Where(b => b.Batch.GrowingAreaId == r.GrowingAreaId.Value);
        }
        if (r.FromDate.HasValue) query = query.Where(b => b.Batch.CreatedAt >= r.FromDate.Value);
        if (r.ToDate.HasValue)
        {
            var exclusiveEnd = r.ToDate.Value.Date.AddDays(1);
            query = query.Where(b => b.Batch.CreatedAt < exclusiveEnd);
        }
        switch (r.ConsumptionStatus)
        {
            case "IN_STOCK": query = query.Where(b => b.Batch.Inventories.Any(i => i.QuantityOnHand > 0) && b.Batch.BatchStatus != "PARTIALLY_ISSUED"); break;
            case "CONSUMING": query = query.Where(b => b.Batch.BatchStatus == "PARTIALLY_ISSUED"); break;
            case "CONSUMED": query = query.Where(b => b.Batch.BatchStatus == "ISSUED"); break;
            case null: case "": break;
            default: throw new ArgumentException("Trạng thái tiêu thụ không hợp lệ.");
        }
        var count = await query.CountAsync(ct);
        var summary = new SupplierBatchSummaryResponse
        {
            TotalDeclaredBatches = count,
            PendingApprovalBatches = await query.CountAsync(b => b.Status == "SUBMITTED" || b.Status == "PENDING_PREDECLARATION", ct),
            PendingQCBatches = await query.CountAsync(b => b.Status == "PENDING_QC", ct),
            ApprovedBatches = await query.CountAsync(b => b.Status == "APPROVED_FOR_STORAGE" || b.Status == "RECEIVED" || b.Status == "IN_STOCK", ct),
            RejectedBatches = await query.CountAsync(b => b.Status == "REJECTED", ct)
        };
        var items = await query.OrderByDescending(b => b.Batch.CreatedAt).ThenByDescending(b => b.Batch.ProductBatchId)
            .Skip(checked((r.PageIndex - 1) * r.PageSize)).Take(r.PageSize)
            .Select(b => new SupplierBatchItemResponse
            {
                BatchId = b.Batch.ProductBatchId, BatchCode = b.Batch.BatchCode, ProductName = b.Batch.ProductName, Note = b.Batch.Note,
                AreaName = b.Batch.GrowingArea.AreaName, Province = b.Batch.GrowingArea.Province,
                District = b.Batch.GrowingArea.District, Ward = b.Batch.GrowingArea.Ward,
                QuantityInTons = b.Batch.WeightInKg / 1000m, SubmittedDate = b.Batch.CreatedAt,
                Status = b.Status,
                ConsumptionStatus = b.Batch.BatchStatus == "ISSUED" ? "CONSUMED" : b.Batch.BatchStatus == "PARTIALLY_ISSUED" ? "CONSUMING" :
                    b.Batch.Inventories.Any(i => i.QuantityOnHand > 0) ? "IN_STOCK" : null
            }).ToListAsync(ct);
        foreach (var item in items)
        {
            item.StatusDisplayName = SupplierBatchStatuses.Label(item.Status);
            item.ConsumptionStatusDisplayName = item.ConsumptionStatus switch
            { "IN_STOCK" => "Tồn kho", "CONSUMING" => "Đang tiêu thụ", "CONSUMED" => "Đã tiêu thụ hết", _ => null };
        }
        return new() { Summary = summary, Batches = new() { Items = items, TotalCount = count, PageIndex = r.PageIndex, PageSize = r.PageSize } };
    }

    public Task<bool> IsCropTypeRegisteredForSupplierAsync(int supplierId, int cropId, CancellationToken ct = default) =>
        db.SupplierCropTypes.AnyAsync(s => s.SupplierId == supplierId && s.CropTypeId == cropId && s.IsActive && s.CropType.IsActive, ct);
    public Task<bool> IsGrowingAreaRegisteredForSupplierAsync(int supplierId, int areaId, CancellationToken ct = default) =>
        db.SupplierGrowingAreas.AnyAsync(s => s.SupplierId == supplierId && s.GrowingAreaId == areaId, ct);
    public Task<string> GenerateBatchCodeAsync(CancellationToken ct = default)
    {
        var localDate = DateOnly.FromDateTime((timeProvider ?? TimeProvider.System)
            .GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Lock the date's row (or its key range on the first allocation of the day).
            // The persisted counter is shared by every supplier and API instance.
            var numbers = await db.Database.SqlQuery<int>($"""
                DECLARE @last int;
                SELECT @last = [LastNumber]
                FROM dbo.PRODUCT_BATCH_DAILY_COUNTER WITH (UPDLOCK, HOLDLOCK)
                WHERE [CodeDate] = {localDate};
                IF @last IS NULL
                BEGIN
                    SET @last = 1;
                    INSERT dbo.PRODUCT_BATCH_DAILY_COUNTER ([CodeDate], [LastNumber]) VALUES ({localDate}, @last);
                END
                ELSE
                BEGIN
                    SET @last = @last + 1;
                    UPDATE dbo.PRODUCT_BATCH_DAILY_COUNTER SET [LastNumber] = @last WHERE [CodeDate] = {localDate};
                END;
                SELECT @last AS [Value];
                """).ToListAsync(ct);
            await transaction.CommitAsync(ct);
            return $"LH-{localDate:yyyyMMdd}-{numbers.Single():D4}";
        });
    }

    public Task AddProductBatchAsync(ProductBatch batch, BatchStatusHistory history, List<string>? urls, CancellationToken ct = default) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await SetActor(history.ChangedByAccountId, ct);
                db.ProductBatches.Add(batch);
                await db.SaveChangesAsync(ct);
                history.ProductBatchId = batch.ProductBatchId;
                await EnsureHistory(history, 0, ct);
                await SupplierFileLinks.ReplaceDocuments(db, history.ChangedByAccountId!.Value, batch.SupplierId, batch.ProductBatchId, urls, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            finally { await SetActor(null, CancellationToken.None); }
        });

    public Task<ProductBatch?> GetBatchByIdAsync(long id, CancellationToken ct = default) =>
        db.ProductBatches.AsNoTracking().SingleOrDefaultAsync(b => b.ProductBatchId == id, ct);

    public Task UpdateProductBatchAsync(ProductBatch desired, BatchStatusHistory history, DateTime expectedCreatedAt,
        DateTime? expectedUpdatedAt, List<string>? urls, CancellationToken ct = default) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // This lock is held until commit and also blocks ordinary staff UPDATE statements.
                var current = await db.ProductBatches.FromSqlInterpolated($"SELECT * FROM dbo.PRODUCT_BATCH WITH (UPDLOCK, HOLDLOCK) WHERE ProductBatchID = {desired.ProductBatchId}")
                    .SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Không tìm thấy lô hàng.");
                if (current.SupplierId != desired.SupplierId || current.BatchStatus != "SUBMITTED" ||
                    current.CreatedAt != expectedCreatedAt || current.UpdatedAt != expectedUpdatedAt)
                    throw new ConflictException("Lô hàng đã thay đổi hoặc đã được tiếp nhận. Vui lòng tải lại dữ liệu.");
                var previousHistory = await db.BatchStatusHistories.Where(h => h.ProductBatchId == current.ProductBatchId)
                    .Select(h => (long?)h.BatchStatusHistoryId).MaxAsync(ct) ?? 0;
                if (desired.BatchStatus == "CANCELLED") current.BatchStatus = "CANCELLED";
                else
                {
                    // Only declaration fields are writable; never copy receiving/QC fields.
                    current.CropTypeId = desired.CropTypeId; current.GrowingAreaId = desired.GrowingAreaId;
                    current.ProductName = desired.ProductName; current.HarvestDate = desired.HarvestDate;
                    current.DeclaredQuantity = desired.DeclaredQuantity; current.Unit = desired.Unit; current.WeightInKg = desired.WeightInKg;
                    current.PackagingType = desired.PackagingType; current.PackageCount = desired.PackageCount;
                    current.PackageUnitWeightKg = desired.PackageUnitWeightKg;
                    current.ExpectedMinTempC = desired.ExpectedMinTempC; current.ExpectedMaxTempC = desired.ExpectedMaxTempC;
                    current.ExpectedMinHumidityPct = desired.ExpectedMinHumidityPct; current.ExpectedMaxHumidityPct = desired.ExpectedMaxHumidityPct;
                    current.ExpectedDeliveryDate = desired.ExpectedDeliveryDate; current.ExpiryDate = desired.ExpiryDate; current.Note = desired.Note;
                    await SupplierFileLinks.ReplaceDocuments(db, history.ChangedByAccountId!.Value, current.SupplierId, current.ProductBatchId, urls, ct);
                }
                // Supports the existing datetime2(0) DB without reusing a version within one second.
                var now = DateTime.UtcNow;
                now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                var previous = current.UpdatedAt ?? current.CreatedAt;
                current.UpdatedAt = now > previous ? now : previous.AddSeconds(1);
                desired.UpdatedAt = current.UpdatedAt;
                await SetActor(history.ChangedByAccountId, ct);
                await db.SaveChangesAsync(ct);
                if (history.OldStatus != history.NewStatus)
                {
                    await EnsureHistory(history, previousHistory, ct);
                    await db.SaveChangesAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            finally { await SetActor(null, CancellationToken.None); }
        });

    private Task SetActor(int? id, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"EXEC sys.sp_set_session_context @key=N'AccountID', @value={id}", ct);
    private async Task EnsureHistory(BatchStatusHistory h, long previousId, CancellationToken ct)
    {
        var exists = await db.BatchStatusHistories.AnyAsync(x => x.ProductBatchId == h.ProductBatchId &&
            x.BatchStatusHistoryId > previousId && x.OldStatus == h.OldStatus && x.NewStatus == h.NewStatus, ct);
        if (!exists) db.BatchStatusHistories.Add(h);
    }
    public Task<ProductBatch?> GetBatchStatusDetailByIdAsync(long id, CancellationToken ct = default) =>
        db.ProductBatches.AsNoTracking().Include(b => b.CropType).Include(b => b.GrowingArea).Include(b => b.QcInspections)
            .Include(b => b.GoodsReceipts.Where(g => g.ReceiptStatus == "COMMITTED")).AsSplitQuery()
            .SingleOrDefaultAsync(b => b.ProductBatchId == id, ct);
    public async Task<decimal> GetCommittedReceivedQuantityAsync(long id, CancellationToken ct = default) =>
        await db.GoodsReceipts.Where(g => g.ProductBatchId == id && g.ReceiptStatus == "COMMITTED")
            .SumAsync(g => (decimal?)g.ReceivedQuantity, ct) ?? 0m;
    public Task<List<BatchStatusHistory>> GetBatchStatusHistoryAsync(long id, CancellationToken ct = default) =>
        db.BatchStatusHistories.AsNoTracking().Include(h => h.ChangedByAccount).Where(h => h.ProductBatchId == id)
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.BatchStatusHistoryId).ToListAsync(ct);
    public async Task<List<SupplierDocumentDto>> GetDocumentsAsync(long id, CancellationToken ct = default)
    {
        var supplierId = await db.ProductBatches.Where(b => b.ProductBatchId == id).Select(b => b.SupplierId).SingleAsync(ct);
        return await SupplierFileLinks.Documents(db, supplierId, id, ct);
    }
}
