using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class OrderReservationAllocator(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor) : IOrderReservationAllocator
{
    public async Task<bool> TryReserveAsync(long orderId, int actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new UnauthorizedAccessException();
        try
        {
            return await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db =>
            {
                var order = await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM PURCHASE_ORDER WITH (UPDLOCK,HOLDLOCK) WHERE PurchaseOrderID={orderId}")
                    .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
                if (await BatchHandlingEligibility.OrderHeldAsync(db, orderId, ct)) return false;
                if (order.OrderStatus is "RESERVED" or "PARTIALLY_PICKED" or "PICKED")
                {
                    var allocated = await db.OrderDetails.AsNoTracking().Where(l => l.PurchaseOrderId == orderId)
                        .Select(l => new { l.ApprovedWeightKg, Reserved = l.InventoryReservations.Where(r => PickingRules.ActiveReservations.Contains(r.ReservationStatus)).Sum(r => (decimal?)r.ReservedQuantity) ?? 0,
                            Picked = l.InventoryReservations.Where(r => PickingRules.ActiveReservations.Contains(r.ReservationStatus)).Sum(r => (decimal?)r.PickedQuantity) ?? 0 }).ToListAsync(ct);
                    return allocated.Count > 0 && allocated.All(l => l.ApprovedWeightKg > 0 && l.Reserved == l.ApprovedWeightKg
                        && (order.OrderStatus != "PICKED" || l.Picked == l.Reserved));
                }
                if (order.OrderStatus != "APPROVED" || order.ApprovedAt is null || order.ApprovedByAccountId is null) return false;
                var lines = await db.OrderDetails.Where(l => l.PurchaseOrderId == orderId).OrderBy(l => l.OrderDetailId).ToListAsync(ct);
                if (lines.Count == 0 || lines.Any(l => l.RequestedProductBatchId is null || !PickingRules.Amount(l.ApprovedWeightKg))) return false;
                var stock = new Dictionary<long, List<Inventory>>();
                // Same selected whole lots as the manager-approved order, no substitution or new sales policy.
                foreach (var batchId in lines.Select(l => l.RequestedProductBatchId!.Value).Distinct().Order())
                {
                    var batch = await WarehouseOperationTransaction.LockBatchAsync(db, batchId, ct);
                    if (await BatchHandlingEligibility.HandlingReasonAsync(db, batchId, ct) is not null) return false;
                    if (batch.BatchStatus != "IN_STOCK" || PickingRules.BatchLockReason(batch.BatchStatus, batch.ExpiryDate) is not null
                        || lines.Any(l => l.RequestedProductBatchId == batchId && l.CropTypeId != batch.CropTypeId)) return false;
                    foreach (var line in lines.Where(l => l.RequestedProductBatchId == batchId)) await BatchHandlingEligibility.ValidateAdjustedSaleAsync(db, batchId, line.ApprovedWeightKg, ct);
                    var inventory = await WarehouseOperationTransaction.LockInventoryAsync(db, batchId, ct);
                    if (inventory.Count == 0 || inventory.Any(i => i.Unit != "kg")) return false;
                    foreach (var locationId in inventory.Select(i => i.WarehouseLocationId).Distinct().Order())
                    {
                        var location = await db.WarehouseLocations.FromSqlInterpolated($"SELECT * FROM WAREHOUSE_LOCATION WITH (UPDLOCK,HOLDLOCK) WHERE WarehouseLocationID={locationId}").SingleAsync(ct);
                        if (location.LocationStatus != "ACTIVE") return false;
                    }
                    stock.Add(batchId, inventory);
                }
                var existing = await db.InventoryReservations.Where(r => r.OrderDetail.PurchaseOrderId == orderId).ToListAsync(ct);
                // An inconsistent legacy allocation must be reviewed rather than silently replaced.
                if (existing.Any(r => !PickingRules.ActiveReservations.Contains(r.ReservationStatus) || r.ReleasedAt != null)) return false;
                var remaining = stock.Values.SelectMany(x => x).ToDictionary(i => i.InventoryId, i => i.QuantityOnHand - i.ReservedQuantity);
                var planned = new List<InventoryReservation>();
                var increases = new Dictionary<InventoryReservation, decimal>();
                foreach (var line in lines)
                {
                    var lineExisting = existing.Where(r => r.OrderDetailId == line.OrderDetailId).ToList();
                    if (lineExisting.Any(r => !stock[line.RequestedProductBatchId!.Value].Any(i => i.InventoryId == r.InventoryId))) return false;
                    var needed = line.ApprovedWeightKg - lineExisting.Sum(r => r.ReservedQuantity);
                    if (needed < 0) return false;
                    foreach (var inventory in stock[line.RequestedProductBatchId!.Value])
                    {
                        if (needed == 0) break;
                        var existingRow = lineExisting.SingleOrDefault(r => r.InventoryId == inventory.InventoryId);
                        // Complete a legacy, not-yet-picked allocation without creating another row
                        // for its unique order-detail/inventory pair. Never expand a picked reservation.
                        if (existingRow is not null && (existingRow.PickedQuantity != 0 || existingRow.ReservationStatus != "RESERVED")) continue;
                        var take = Math.Min(needed, remaining[inventory.InventoryId]);
                        if (take <= 0) continue;
                        if (existingRow is not null) increases.Add(existingRow, take);
                        else planned.Add(new InventoryReservation { OrderDetailId = line.OrderDetailId, InventoryId = inventory.InventoryId,
                            ReservedQuantity = take, PickedQuantity = 0, ReservationStatus = "RESERVED", ReservedAt = DateTime.UtcNow });
                        remaining[inventory.InventoryId] -= take; needed -= take;
                    }
                    if (needed != 0) return false; // Nothing has been written: never leave half an allocation.
                }
                foreach (var (reservation, quantity) in increases) reservation.ReservedQuantity += quantity;
                db.InventoryReservations.AddRange(planned);
                await db.SaveChangesAsync(ct);
                // TR_INVENTORY_RESERVATION_SYNC owns Inventory.ReservedQuantity and line kg counters.
                var totals = await db.OrderDetails.AsNoTracking().Where(l => l.PurchaseOrderId == orderId)
                    .Select(l => new { l.ApprovedWeightKg, l.ReservedWeightKg }).ToListAsync(ct);
                if (totals.Any(l => l.ReservedWeightKg != l.ApprovedWeightKg))
                    throw new ConflictException("Đồng bộ giữ hàng chưa sẵn sàng; đơn vẫn chờ phân bổ.");
                var effective = existing.Concat(planned).ToList();
                order.OrderStatus = effective.All(r => r.PickedQuantity == r.ReservedQuantity) ? "PICKED"
                    : effective.Any(r => r.PickedQuantity > 0) ? "PARTIALLY_PICKED" : "RESERVED";
                order.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return true;
            }, ct);
        }
        catch (ConflictException) { return false; } // Transaction rolled back, previous APPROVED remains committed.
    }
}
