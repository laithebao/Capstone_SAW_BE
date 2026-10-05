using System.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class GoodsReceiptRepository(DbContextOptions<AppDbContext> options,
    IHttpContextAccessor accessor) : IGoodsReceiptRepository
{
    public async Task<long> CreateAsync(CreateGoodsReceiptRequest request, int actorId, CancellationToken ct)
    {
        // Stable across execution-strategy retries, including an unknown commit outcome.
        var code = "GR-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        return await ExecuteAsync(async db =>
        {
            var previousAttempt = await db.GoodsReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.ReceiptCode == code, ct);
            if (previousAttempt is not null) return previousAttempt.GoodsReceiptId;
            var batch = await LockEligibleBatchAsync(db, request.ProductBatchId, ct);
            var existing = await LockReceipts(db, batch.ProductBatchId).AsNoTracking().FirstOrDefaultAsync(ct);
            if (existing is not null)
                throw new ConflictException($"Lô đã có phiếu {existing.ReceiptCode} (ID {existing.GoodsReceiptId}). Vui lòng mở phiếu này từ danh sách nhập kho.");
            await ActiveLocationAsync(db, request.WarehouseLocationId, ct);
            var receipt = new GoodsReceipt
            {
                ReceiptCode = code, ProductBatchId = batch.ProductBatchId,
                WarehouseLocationId = request.WarehouseLocationId, OperationAccountId = actorId,
                ReceivedQuantity = batch.VerifiedQuantity!.Value, Unit = batch.Unit,
                WeightInKg = batch.VerifiedWeightInKg!.Value, ReceiptStatus = "DRAFT",
                ReceivedAt = request.ReceivedDate.ToDateTime(TimeOnly.MinValue), Note = request.Note
            };
            db.GoodsReceipts.Add(receipt);
            await db.SaveChangesAsync(ct); // Existing AppDbContext audit captures actor and snapshot.
            return receipt.GoodsReceiptId;
        }, actorId, ct);
    }

    public async Task UpdateDraftAsync(long id, UpdateGoodsReceiptDraftRequest request, int actorId, CancellationToken ct)
    {
        await ExecuteAsync(async db =>
        {
            var receipt = await LockReceipt(db, id).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy phiếu nhập kho.");
            if (receipt.ReceiptStatus != "DRAFT")
                throw new ConflictException("Phiếu đã xác nhận nhập kho, không thể sửa. Vui lòng tải lại phiếu.");
            EnsureSnapshot(receipt, request.ExpectedSnapshot);
            await ActiveLocationAsync(db, request.WarehouseLocationId, ct);
            // Explicit whitelist. Do not attach a client entity or update batch/stock/history.
            receipt.WarehouseLocationId = request.WarehouseLocationId;
            receipt.ReceivedAt = request.ReceivedDate.ToDateTime(TimeOnly.MinValue);
            receipt.Note = request.Note;
            await db.SaveChangesAsync(ct); // Existing audit captures the actual actor and before/after.
            return true;
        }, actorId, ct);
    }

    public async Task ConfirmAsync(long id, GoodsReceiptSnapshot expectedSnapshot, int actorId, CancellationToken ct)
    {
        await ExecuteAsync(async db =>
        {
            // Serialize stock writers for warehouse-wide capacity, including an empty inventory.
            // A SQL table lock also protects against other modules that do not use sp_getapplock.
            // Keep it within this short transaction; no external calls/uploads occur here.
            await db.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX, HOLDLOCK)", ct);
            var reference = await LockReceipt(db, id).AsNoTracking().SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy phiếu nhập kho.");
            if (reference.ReceiptStatus == "COMMITTED") return true;
            EnsureSnapshot(reference, expectedSnapshot);
            var batch = await LockEligibleBatchAsync(db, reference.ProductBatchId, ct);
            var receipts = await LockReceipts(db, batch.ProductBatchId).ToListAsync(ct);
            var receipt = receipts.Single(r => r.GoodsReceiptId == id);
            if (receipt.ReceiptStatus != "DRAFT" || receipts.Any(r => r.GoodsReceiptId != id && r.ReceiptStatus == "COMMITTED"))
                throw new ConflictException("Lô đã được nhập kho hoặc phiếu không còn ở trạng thái nháp.");
            if (receipt.ReceivedQuantity != batch.VerifiedQuantity || receipt.WeightInKg != batch.VerifiedWeightInKg || receipt.Unit != batch.Unit)
                throw new ConflictException("Số liệu kiểm nhận đã thay đổi so với phiếu nháp. Vui lòng tải lại và kiểm tra; không thể xác nhận phiếu này.");
            var location = await ActiveLocationAsync(db, receipt.WarehouseLocationId, ct);
            if (await db.Inventories.AnyAsync(i => i.Unit != "kg", ct))
                throw new ConflictException("Tồn kho có đơn vị không tương thích với kg. Vui lòng kiểm tra dữ liệu kho.");
            var total = await db.Inventories.SumAsync(i => (decimal?)i.QuantityOnHand, ct) ?? 0;
            var local = await db.Inventories.Where(i => i.WarehouseLocationId == location.WarehouseLocationId)
                .SumAsync(i => (decimal?)i.QuantityOnHand, ct) ?? 0;
            GoodsReceiptRules.EnsureCapacity(local, receipt.WeightInKg, location.MaxWeightKg, "Vị trí kho");
            foreach (var setting in await db.WarehouseSettings.AsNoTracking().ToListAsync(ct))
                GoodsReceiptRules.EnsureCapacity(total, receipt.WeightInKg, setting.MaxCapacityKg, "Kho");

            var stock = await db.Inventories.SingleOrDefaultAsync(i => i.ProductBatchId == batch.ProductBatchId
                && i.WarehouseLocationId == receipt.WarehouseLocationId, ct);
            var now = DateTime.UtcNow;
            if (stock is null)
            {
                stock = new Inventory { ProductBatchId = batch.ProductBatchId, WarehouseLocationId = receipt.WarehouseLocationId, Unit = "kg" };
                db.Inventories.Add(stock);
            }
            if (!GoodsReceiptRules.ValidAmount(stock.QuantityOnHand + receipt.WeightInKg))
                throw new ConflictException("Tổng khối lượng tồn vượt giới hạn lưu trữ.");
            stock.QuantityOnHand += receipt.WeightInKg;
            stock.LastUpdatedAt = now;
            receipt.ReceiptStatus = "COMMITTED"; receipt.CommittedAt = now;
            var previousHistoryId = await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId)
                .MaxAsync(h => (long?)h.BatchStatusHistoryId, ct) ?? 0;
            batch.BatchStatus = "IN_STOCK"; batch.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            // Existing history trigger is immutable; never edit its result or insert a duplicate.
            if (!await db.BatchStatusHistories.AnyAsync(h => h.ProductBatchId == batch.ProductBatchId
                    && h.BatchStatusHistoryId > previousHistoryId && h.NewStatus == "IN_STOCK", ct))
            {
                db.BatchStatusHistories.Add(new BatchStatusHistory { ProductBatchId = batch.ProductBatchId,
                    OldStatus = "APPROVED_FOR_STORAGE", NewStatus = "IN_STOCK", ChangedByAccountId = actorId,
                    ChangeReason = "Xác nhận nhập kho " + receipt.ReceiptCode, ChangedAt = now });
                await db.SaveChangesAsync(ct);
            }
            return true;
        }, actorId, ct);
    }

    private async Task<T> ExecuteAsync<T>(Func<AppDbContext, Task<T>> action, int actorId, CancellationToken ct)
    {
        await using var strategyContext = new AppDbContext(options, accessor);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Never reuse tracked entities from a rolled-back attempt.
            await using var db = new AppDbContext(options, accessor);
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'AccountID', @value={actorId}", ct);
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var result = await action(db);
                await tx.CommitAsync(ct);
                return result;
            }
            finally
            {
                if (db.Database.GetDbConnection().State == ConnectionState.Open)
                    await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL", CancellationToken.None);
                await db.Database.CloseConnectionAsync();
            }
        });
    }

    private static IQueryable<GoodsReceipt> LockReceipts(AppDbContext db, long batchId) => db.GoodsReceipts
        .FromSqlInterpolated($"SELECT * FROM GOODS_RECEIPT WITH (UPDLOCK, HOLDLOCK) WHERE ProductBatchID={batchId}");

    private static IQueryable<GoodsReceipt> LockReceipt(AppDbContext db, long id) => db.GoodsReceipts
        .FromSqlInterpolated($"SELECT * FROM GOODS_RECEIPT WITH (UPDLOCK, HOLDLOCK) WHERE GoodsReceiptID={id}");

    private static void EnsureSnapshot(GoodsReceipt receipt, GoodsReceiptSnapshot expected)
    {
        if (expected is null || receipt.WarehouseLocationId != expected.WarehouseLocationId
            || receipt.ReceivedAt != expected.ReceivedAt || !string.Equals(receipt.Note, expected.Note, StringComparison.Ordinal))
            throw new ConflictException("Phiếu nháp đã được người khác thay đổi. Vui lòng tải lại, kiểm tra vị trí, ngày nhận và ghi chú trước khi tiếp tục.");
    }

    private static async Task<ProductBatch> LockEligibleBatchAsync(AppDbContext db, long batchId, CancellationToken ct)
    {
        // Protect the latest inspection, plus the absent/new-inspection key range until commit.
        var latest = await db.QcInspections.FromSqlInterpolated($"SELECT * FROM QC_INSPECTION WITH (UPDLOCK, HOLDLOCK) WHERE ProductBatchID={batchId}")
            .AsNoTracking().OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId).FirstOrDefaultAsync(ct);
        var batch = await db.ProductBatches.FromSqlInterpolated($"SELECT * FROM PRODUCT_BATCH WITH (UPDLOCK, HOLDLOCK) WHERE ProductBatchID={batchId}")
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy lô hàng.");
        GoodsReceiptRules.EnsureEligible(batch, latest);
        return batch;
    }

    private static async Task<WarehouseLocation> ActiveLocationAsync(AppDbContext db, int id, CancellationToken ct)
    {
        var location = await db.WarehouseLocations.AsNoTracking().SingleOrDefaultAsync(l => l.WarehouseLocationId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy vị trí kho.");
        if (location.LocationStatus != "ACTIVE") throw new ConflictException("Vị trí kho đã bị khóa. Vui lòng chọn vị trí đang hoạt động.");
        return location;
    }
}
