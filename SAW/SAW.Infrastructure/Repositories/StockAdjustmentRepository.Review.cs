using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    private sealed record ReviewReceipt(Guid RequestId, string PayloadHash, long AdjustmentId, string Decision, object? Values);

    public async Task ReviewAsync(long id, ReviewStockAdjustmentRequest request, int actorId, CancellationToken ct)
    {
        await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db => {
            // Check current DB authority as well as the controller's authenticated role claims.
            if (!await db.Accounts.AnyAsync(a => a.AccountId == actorId && a.AccountStatus == "ACTIVE"
                    && a.Role.RoleCode == "WAREHOUSE_MANAGER" && a.Role.IsActive, ct))
                throw new ForbiddenException("Chỉ quản lý kho đang hoạt động được duyệt hoặc từ chối yêu cầu.");
            var key = "stock-adjustment-review:" + request.RequestId;
            var lockResult = await db.Database.SqlQuery<int>($"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource={key},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r AS Value;").ToListAsync(ct);
            if (lockResult.Single() < 0) throw new ConflictException("Quyết định đang được xử lý. Vui lòng kiểm tra lại kết quả hoặc thử lại cùng dữ liệu.");
            // Same warehouse capacity lock as Confirm Goods Receipt; it also prevents phantom
            // inventory locations and protects against writers without an application lock.
            await db.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX,HOLDLOCK)", ct);
            var reference = await db.StockAdjustments.AsNoTracking().Where(a => a.StockAdjustmentId == id)
                .Select(a => new { a.Inventory.ProductBatchId }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy phiếu điều chỉnh.");
            var batch = await WarehouseOperationTransaction.LockBatchAsync(db, reference.ProductBatchId, ct);
            var a = await db.StockAdjustments.FromSqlInterpolated($"SELECT * FROM STOCK_ADJUSTMENT WITH (UPDLOCK,HOLDLOCK) WHERE StockAdjustmentID={id}").SingleAsync(ct);
            var payload = Hash(JsonSerializer.Serialize(new { Id = id, Request = request }));
            var operationId = request.RequestId.ToString();
            var audits = await db.AuditLogs.AsNoTracking().Where(l => l.EntityName == "STOCK_ADJUSTMENT" && l.ActionType == "REVIEW_STOCK_ADJUSTMENT"
                && l.NewDataJson != null && l.NewDataJson.Contains(operationId)).ToListAsync(ct);
            foreach (var log in audits)
            {
                var receipt = JsonSerializer.Deserialize<ReviewReceipt>(log.NewDataJson!);
                if (receipt?.RequestId != request.RequestId) continue;
                if (log.AccountId != actorId || receipt.PayloadHash != payload || receipt.AdjustmentId != id)
                    throw new ConflictException("Mã thao tác đã được dùng cho quyết định khác.");
                return true;
            }
            if (a.AdjustmentStatus != "PENDING") throw new ConflictException("Yêu cầu đã được người khác xử lý. Vui lòng tải lại kết quả.");
            var stock = await WarehouseOperationTransaction.LockInventoryAsync(db, batch.ProductBatchId, ct);
            foreach (var locationId in stock.Select(i => i.WarehouseLocationId).Distinct().Order())
                await db.WarehouseLocations.FromSqlInterpolated($"SELECT * FROM WAREHOUSE_LOCATION WITH (UPDLOCK,HOLDLOCK) WHERE WarehouseLocationID={locationId}").LoadAsync(ct);
            await db.WarehouseSettings.FromSqlRaw("SELECT * FROM WAREHOUSE_SETTING WITH (UPDLOCK,HOLDLOCK)").LoadAsync(ct);
            var ids = stock.Select(i => i.InventoryId).ToArray();
            await db.InventoryReservations.FromSqlRaw("SELECT * FROM INVENTORY_RESERVATION WITH (UPDLOCK,HOLDLOCK)")
                .Where(r => ids.Contains(r.InventoryId)).OrderBy(r => r.InventoryReservationId).LoadAsync(ct);
            var before = await ReadReviewAsync(db, id, ct);
            if (before.ReviewSnapshot != request.ExpectedSnapshot)
                throw new ConflictException("Yêu cầu hoặc số liệu đang xem đã thay đổi. Vui lòng tải lại và kiểm tra trước khi quyết định.");
            var now = DateTime.UtcNow;
            if (request.Decision == "APPROVE")
            {
                if (!before.CanApprove) throw new ConflictException(before.ApprovalLockReason!);
                var selected = a.RequestKind == "DISPOSAL" ? stock : stock.Where(i => i.InventoryId == a.InventoryId).ToList();
                foreach (var i in selected) { i.QuantityOnHand = a.RequestKind == "DISPOSAL" ? 0 : a.ActualQuantity; i.LastUpdatedAt = now; }
                var oldStatus = batch.BatchStatus;
                var historyId = await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId).MaxAsync(h => (long?)h.BatchStatusHistoryId, ct) ?? 0;
                if (a.RequestKind == "DISPOSAL") { batch.BatchStatus = "DISPOSED"; batch.UpdatedAt = now; }
                // Required ordering: persist inventory + DISPOSED BEFORE the request guard
                // observes APPROVED. All saves remain within the same rollback boundary.
                await db.SaveChangesAsync(ct);
                if (a.RequestKind == "DISPOSAL" && !await db.BatchStatusHistories.AnyAsync(h => h.ProductBatchId == batch.ProductBatchId
                        && h.BatchStatusHistoryId > historyId && h.NewStatus == "DISPOSED", ct))
                {
                    db.BatchStatusHistories.Add(new BatchStatusHistory { ProductBatchId = batch.ProductBatchId, OldStatus = oldStatus,
                        NewStatus = "DISPOSED", ChangedByAccountId = actorId, ChangedAt = now, ChangeReason = "Duyệt loại bỏ " + a.AdjustmentCode });
                }
            }
            a.AdjustmentStatus = request.Decision == "APPROVE" ? "APPROVED" : "REJECTED";
            a.ReviewedByAccountId = actorId; a.ReviewedAt = now; a.ReviewNote = request.ReviewNote;
            db.AuditLogs.Add(new AuditLog { AccountId = actorId, EntityName = "STOCK_ADJUSTMENT", EntityId = id.ToString(),
                ActionType = "REVIEW_STOCK_ADJUSTMENT", CreatedAt = now, OldDataJson = JsonSerializer.Serialize(before),
                NewDataJson = JsonSerializer.Serialize(new ReviewReceipt(request.RequestId, payload, id, request.Decision,
                    new { a.AdjustmentStatus, a.ReviewedByAccountId, a.ReviewedAt, a.ReviewNote, batch.BatchStatus,
                        Inventory = stock.OrderBy(i => i.InventoryId).Select(i => new { i.InventoryId, i.QuantityOnHand, i.ReservedQuantity, i.LastUpdatedAt }).ToList() })),
                Description = (request.Decision == "REJECT" ? "Từ chối yêu cầu; không thay đổi tồn. " : a.RequestKind == "DISPOSAL" ? "Duyệt loại bỏ toàn bộ lô. " : "Duyệt kiểm đếm; gán tồn bằng số thực đếm. ") + request.ReviewNote });
            await db.SaveChangesAsync(ct); return true;
        }, ct);
    }
}
