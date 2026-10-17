using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.Picking;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor,
    IQrImageStorage storage) : IStockAdjustmentRepository
{
    private AppDbContext Context() => new(options, accessor);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public Task<long> CreateAsync(CreateStockAdjustmentRequest request, int actorId, CancellationToken ct) =>
        WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db =>
        {
            // Serialize the operation key across batches as well as retries of the same request.
            var resource = "stock-adjustment:" + request.RequestId;
            var lockResult = await db.Database.SqlQuery<int>($"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource={resource},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r AS Value;").ToListAsync(ct);
            if (lockResult.Single() < 0) throw new ConflictException("Yêu cầu đang được xử lý. Vui lòng thử lại cùng dữ liệu.");
            var payloadHash = Hash(JsonSerializer.Serialize(request));
            var previous = await db.StockAdjustments.SingleOrDefaultAsync(a => a.RequestId == request.RequestId, ct);
            if (previous is not null)
            {
                if (previous.CreatedByAccountId != actorId || previous.RequestPayloadHash != payloadHash)
                    throw new ConflictException("Mã thao tác đã được dùng cho yêu cầu khác. Vui lòng tải lại.");
                return previous.StockAdjustmentId;
            }
            var batch = await WarehouseOperationTransaction.LockBatchAsync(db, request.ProductBatchId, ct);
            var inventory = await WarehouseOperationTransaction.LockInventoryAsync(db, batch.ProductBatchId, ct);
            foreach (var location in inventory.Select(i => i.WarehouseLocationId).Distinct().Order())
                await db.WarehouseLocations.FromSqlInterpolated($"SELECT * FROM WAREHOUSE_LOCATION WITH (UPDLOCK,HOLDLOCK) WHERE WarehouseLocationID={location}").LoadAsync(ct);
            var scope = await ReadScopeAsync(db, batch, inventory, request.RequestKind, request.InventoryId, ct);
            if (!scope.CanRequest) throw new ConflictException(scope.LockReason!);
            if (scope.Snapshot != request.ExpectedSnapshot) throw new ConflictException("Tồn kho hoặc phạm vi vị trí đã thay đổi. Vui lòng tải lại số liệu và kiểm tra trước khi gửi.");
            var selected = request.RequestKind == "DISPOSAL" ? inventory : inventory.Where(i => i.InventoryId == request.InventoryId).ToList();
            var system = selected.Sum(i => i.QuantityOnHand);
            var actual = request.RequestKind == "DISPOSAL" ? 0 : request.ActualQuantityKg!.Value;
            if (actual == system) throw new BadRequestException("Không có chênh lệch tồn kho.");
            if (request.RequestKind == "COUNT" && actual < selected.Single().ReservedQuantity)
                throw new ConflictException("Lượng thực đếm thấp hơn lượng đang giữ; cần xử lý phần giữ hàng trước khi gửi điều chỉnh.");
            if (!PickingRules.Amount(system, true)) throw new ConflictException("Tổng tồn vượt giới hạn decimal(18,3) của phiếu.");
            var images = new List<StockAdjustmentImage>();
            foreach (var imageId in request.ImageIds.Order())
            {
                var image = await db.StockAdjustmentImages.FromSqlInterpolated($"SELECT * FROM STOCK_ADJUSTMENT_IMAGE WITH (UPDLOCK,HOLDLOCK) WHERE StockAdjustmentImageId={imageId}").SingleOrDefaultAsync(ct);
                if (image is null || image.UploadedByAccountId != actorId || image.StockAdjustmentId != null)
                    throw new ConflictException("Ảnh không thuộc tài khoản hiện tại hoặc đã gắn với yêu cầu khác.");
                images.Add(image);
            }
            var adjustment = new StockAdjustment {
                AdjustmentCode = "SA-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), InventoryId = selected.First().InventoryId,
                RequestKind = request.RequestKind, RequestId = request.RequestId, RequestPayloadHash = payloadHash,
                InventorySnapshotJson = await SnapshotJsonAsync(db, batch, selected, ct),
                SystemQuantity = system, ActualQuantity = actual, AdjustmentType = request.AdjustmentType,
                Reason = request.Reason, AdjustmentStatus = "PENDING", CreatedByAccountId = actorId, CreatedAt = DateTime.UtcNow,
                Lines = selected.Select(i => new StockAdjustmentLine { InventoryId = i.InventoryId, SystemQuantity = i.QuantityOnHand,
                    ActualQuantity = request.RequestKind == "DISPOSAL" ? 0 : actual, ReservedQuantity = i.ReservedQuantity,
                    InventoryUpdatedAt = i.LastUpdatedAt, Unit = i.Unit, LocationCode = i.WarehouseLocation.LocationCode }).ToList(),
                Images = images
            };
            db.StockAdjustments.Add(adjustment);
            await db.SaveChangesAsync(ct);
            db.AuditLogs.Add(new AuditLog { AccountId = actorId, EntityName = "STOCK_ADJUSTMENT", EntityId = adjustment.StockAdjustmentId.ToString(),
                ActionType = "CREATE_STOCK_ADJUSTMENT", CreatedAt = DateTime.UtcNow,
                NewDataJson = JsonSerializer.Serialize(new { request.RequestId, adjustment.AdjustmentCode, request.RequestKind,
                    request.ProductBatchId, adjustment.InventorySnapshotJson, adjustment.SystemQuantity, adjustment.ActualQuantity,
                    adjustment.AdjustmentType, adjustment.Reason, ImageIds = request.ImageIds, Status = "PENDING" }),
                Description = "Gửi yêu cầu chờ Warehouse Manager duyệt; tồn kho và lượng giữ chưa thay đổi." });
            await db.SaveChangesAsync(ct);
            return adjustment.StockAdjustmentId;
        }, ct);

    public async Task<AdjustmentScope> ScopeAsync(long batchId, string kind, long? inventoryId, CancellationToken ct)
    {
        await using var db = Context();
        // A serializable read produces one coherent snapshot, without recording any operation.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var batch = await db.ProductBatches.AsNoTracking().SingleOrDefaultAsync(b => b.ProductBatchId == batchId, ct)
                ?? throw new NotFoundException("Không tìm thấy lô hàng.");
            var stock = await db.Inventories.AsNoTracking().Include(i => i.WarehouseLocation).Where(i => i.ProductBatchId == batchId).OrderBy(i => i.InventoryId).ToListAsync(ct);
            var result = await ReadScopeAsync(db, batch, stock, kind, inventoryId, ct); await tx.CommitAsync(ct); return result;
        });
    }
    private static async Task<AdjustmentScope> ReadScopeAsync(AppDbContext db, ProductBatch batch, List<Inventory> inventory,
        string kind, long? inventoryId, CancellationToken ct)
    {
        var selected = kind == "COUNT" && inventoryId != null ? inventory.Where(i => i.InventoryId == inventoryId).ToList() : inventory;
        if (inventoryId != null && selected.Count == 0) throw new BadRequestException("Vị trí tồn kho không thuộc lô đã chọn.");
        var pending = await db.StockAdjustments.AsNoTracking().Where(a => a.AdjustmentStatus == "PENDING" && a.Inventory.ProductBatchId == batch.ProductBatchId)
            .Select(a => new { a.RequestKind, a.InventoryId }).ToListAsync(ct);
        var reason = batch.BatchStatus is "CANCELLED" or "DISPOSED" or "DELETED" ? "Lô đã hủy hoặc đã loại bỏ; không thể lập yêu cầu."
            : selected.Count == 0 ? "Lô chưa có tồn kho."
            : selected.Any(i => i.Unit != "kg") ? "Đơn vị tồn kho phải là kg."
            : kind == "COUNT" && selected.Any(i => i.WarehouseLocation.LocationStatus != "ACTIVE") ? "Vị trí kho không hoạt động."
            : kind == "DISPOSAL" && selected.Sum(i => i.QuantityOnHand) <= 0 ? "Lô không còn tồn để đề nghị loại bỏ."
            : pending.Any(a => kind == "DISPOSAL" || a.RequestKind == "DISPOSAL" || inventoryId == a.InventoryId)
                ? "Phạm vi tồn kho đã có yêu cầu đang chờ duyệt."
            : null;
        return new(new(batch.ProductBatchId, batch.BatchCode, batch.ProductName, batch.ExpiryDate, inventory.Sum(i => i.QuantityOnHand)),
            selected.Select(i => new AdjustmentStock(i.InventoryId, i.WarehouseLocationId, i.WarehouseLocation.LocationCode,
                i.WarehouseLocation.LocationStatus, i.QuantityOnHand, i.ReservedQuantity, i.LastUpdatedAt)).ToList(),
            Hash(await SnapshotJsonAsync(db, batch, selected, ct)), reason is null, reason);
    }
    private static async Task<string> SnapshotJsonAsync(AppDbContext db, ProductBatch batch, List<Inventory> stock, CancellationToken ct)
    {
        var ids = stock.Select(i => i.InventoryId).ToArray();
        var reservations = await db.InventoryReservations.AsNoTracking().Where(r => ids.Contains(r.InventoryId)).OrderBy(r => r.InventoryReservationId)
            .Select(r => new { r.InventoryReservationId, r.InventoryId, r.OrderDetailId, r.ReservedQuantity, r.PickedQuantity, r.ReservationStatus, r.ReleasedAt }).ToListAsync(ct);
        return JsonSerializer.Serialize(new { batch.ProductBatchId, batch.BatchStatus, batch.ExpiryDate,
            Inventory = stock.OrderBy(i => i.InventoryId).Select(i => new { i.InventoryId, i.WarehouseLocationId, i.QuantityOnHand, i.ReservedQuantity,
                i.LastUpdatedAt, i.Unit, i.WarehouseLocation.LocationStatus }).ToList(), Reservations = reservations });
    }
}
