using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.WarehouseInventory;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class WarehouseInventoryRepository(AppDbContext db) : IWarehouseInventoryRepository
{
    public async Task<WarehouseDistributorOrderPage> GetDistributorOrdersAsync(string? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Clamp(page, 1, 1000); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.PurchaseOrders.AsNoTracking().Include(o => o.Distributor).Include(o => o.OrderDetails).ThenInclude(d => d.CropType).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(o => o.OrderStatus == status.ToUpper());
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(o => o.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(o => new WarehouseDistributorOrderSummary(o.PurchaseOrderId, o.OrderCode, o.OrderStatus, o.Distributor.DistributorName, o.OrderDetails.Count, o.TotalAmount, o.CreatedAt, true)).ToList();
        return new(items, total, page, pageSize);
    }

    public async Task<WarehouseDistributorOrderDetail> GetDistributorOrderAsync(long id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Distributor).Include(o => o.OrderDetails).ThenInclude(d => d.CropType).Include(o => o.OrderDetails).ThenInclude(d => d.RequestedProductBatch).SingleOrDefaultAsync(o => o.PurchaseOrderId == id, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy đơn nhà phân phối.");
        return await ToOrderDetail(order, ct);
    }

    public async Task<WarehouseDistributorOrderDetail> ApproveDistributorOrderAsync(int actorAccountId, long id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.Include(o => o.Distributor).Include(o => o.OrderDetails).ThenInclude(d => d.CropType).Include(o => o.OrderDetails).ThenInclude(d => d.RequestedProductBatch).SingleOrDefaultAsync(o => o.PurchaseOrderId == id, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy đơn nhà phân phối.");
        if (order.OrderStatus != "PENDING") throw new InvalidOperationException("Chỉ đơn đang chờ duyệt mới được phê duyệt.");
        var detail = await ToOrderDetail(order, ct);
        if (!detail.StockAvailable) throw new InvalidOperationException("Tồn kho hiện tại không đủ để phê duyệt đơn.");
        order.OrderStatus = "APPROVED"; order.ApprovedAt = DateTime.UtcNow; order.ApprovedByAccountId = actorAccountId; order.UpdatedAt = DateTime.UtcNow;
        db.OrderStatusHistories.Add(new() { PurchaseOrderId = id, OldStatus = "PENDING", NewStatus = "APPROVED", ChangedByAccountId = actorAccountId, ChangedAt = DateTime.UtcNow, ChangeReason = "Warehouse Manager approved" });
        await db.SaveChangesAsync(ct);
        return await ToOrderDetail(order, ct);
    }

    private async Task<WarehouseDistributorOrderDetail> ToOrderDetail(SAW.Domain.Entities.PurchaseOrder order, CancellationToken ct)
    {
        var cropIds = order.OrderDetails.Where(d => d.RequestedProductBatchId == null).Select(d => d.CropTypeId).ToList();
        var batchIds = order.OrderDetails.Where(d => d.RequestedProductBatchId != null).Select(d => d.RequestedProductBatchId!.Value).ToList();
        var stock = await db.Inventories.AsNoTracking().Where(i => batchIds.Contains(i.ProductBatchId) || cropIds.Contains(i.ProductBatch.CropTypeId)).GroupBy(i => new { i.ProductBatchId, i.ProductBatch.CropTypeId }).Select(g => new { g.Key.ProductBatchId, g.Key.CropTypeId, Available = g.Sum(i => i.AvailableQuantity) }).ToListAsync(ct);
        var lines = order.OrderDetails.Select(d => { var available = stock.Where(s => d.RequestedProductBatchId.HasValue ? s.ProductBatchId == d.RequestedProductBatchId : s.CropTypeId == d.CropTypeId).Sum(s => s.Available); return new WarehouseDistributorOrderLine(d.OrderDetailId, d.CropType.CropName, d.RequestedProductBatch?.BatchCode, d.RequestedWeightKg, available, d.UnitPrice, available >= d.RequestedWeightKg); }).ToList();
        return new(order.PurchaseOrderId, order.OrderCode, order.OrderStatus, order.Distributor.DistributorName, order.TotalAmount, order.CreatedAt, order.ExpectedDeliveryDate, lines.All(l => l.StockAvailable), lines);
    }
    public async Task<WarehouseInventoryLevelChart> GetLevelChartAsync(CancellationToken ct)
    {
        var locations = await GetLocationLevelsAsync(ct);

        return new WarehouseInventoryLevelChart(
            locations.Sum(location => location.QuantityOnHandKg),
            locations.Sum(location => location.AvailableQuantityKg),
            locations.Sum(location => location.ReservedQuantityKg),
            locations.Count(location => location.LocationStatus == "ACTIVE"),
            locations.Sum(location => location.LowStockItemCount),
            DateTime.UtcNow,
            locations);
    }

    public async Task<WarehouseCapacityChart> GetCapacityChartAsync(CancellationToken ct)
    {
        var inventoryLocations = await GetLocationLevelsAsync(ct);
        var locations = inventoryLocations.Select(location =>
        {
            var available = location.MaxWeightKg.HasValue
                ? Math.Max(location.MaxWeightKg.Value - location.QuantityOnHandKg, 0)
                : (decimal?)null;
            var status = location.UtilizationPercent switch
            {
                > 100 => "OVERCROWDED",
                >= 90 => "NEAR_CAPACITY",
                _ => "AVAILABLE"
            };
            return new WarehouseCapacityLocation(
                location.LocationId,
                location.LocationCode,
                location.ZoneName,
                location.QuantityOnHandKg,
                location.MaxWeightKg,
                available,
                location.UtilizationPercent,
                status);
        }).ToList();

        var used = locations.Sum(location => location.UsedWeightKg);
        var configuredCapacity = locations.Sum(location => location.MaxWeightKg ?? 0);
        return new WarehouseCapacityChart(
            used,
            configuredCapacity,
            Math.Max(configuredCapacity - used, 0),
            configuredCapacity > 0 ? Math.Round(used / configuredCapacity * 100, 1) : 0,
            locations.Count(location => location.Status == "NEAR_CAPACITY"),
            locations.Count(location => location.Status == "OVERCROWDED"),
            DateTime.UtcNow,
            locations);
    }

    public async Task<ProductQualityDistributionChart> GetQualityDistributionChartAsync(CancellationToken ct)
    {
        var rawGrades = await db.Inventories
            .AsNoTracking()
            .Where(inventory => inventory.QuantityOnHand > 0)
            .GroupBy(inventory => inventory.ProductBatch.QualityGrade ?? "UNGRADED")
            .Select(group => new
            {
                Grade = group.Key,
                BatchCount = group.Select(inventory => inventory.ProductBatchId).Distinct().Count(),
                QuantityOnHandKg = group.Sum(inventory => inventory.QuantityOnHand)
            })
            .OrderByDescending(item => item.QuantityOnHandKg)
            .ToListAsync(ct);

        var totalBatchCount = await db.Inventories
            .AsNoTracking()
            .Where(inventory => inventory.QuantityOnHand > 0)
            .Select(inventory => inventory.ProductBatchId)
            .Distinct()
            .CountAsync(ct);
        var totalQuantity = rawGrades.Sum(item => item.QuantityOnHandKg);
        var grades = rawGrades.Select(item => new ProductQualityDistributionItem(
            item.Grade,
            item.BatchCount,
            item.QuantityOnHandKg,
            totalQuantity > 0 ? Math.Round(item.QuantityOnHandKg / totalQuantity * 100, 1) : 0)).ToList();
        var ungradedCount = rawGrades
            .Where(item => item.Grade == "UNGRADED")
            .Sum(item => item.BatchCount);

        return new ProductQualityDistributionChart(
            totalBatchCount,
            totalQuantity,
            Math.Max(totalBatchCount - ungradedCount, 0),
            ungradedCount,
            grades.FirstOrDefault(item => item.Grade != "UNGRADED")?.Grade,
            DateTime.UtcNow,
            grades);
    }

    private Task<List<InventoryLocationLevel>> GetLocationLevelsAsync(CancellationToken ct) =>
        db.WarehouseLocations
            .AsNoTracking()
            .OrderBy(location => location.LocationCode)
            .Select(location => new InventoryLocationLevel(
                location.WarehouseLocationId,
                location.LocationCode,
                location.ZoneName,
                location.RackName,
                location.BinName,
                location.LocationStatus,
                location.Inventories.Sum(inventory => (decimal?)inventory.QuantityOnHand) ?? 0,
                location.Inventories.Sum(inventory => (decimal?)inventory.ReservedQuantity) ?? 0,
                location.Inventories.Sum(inventory => (decimal?)inventory.AvailableQuantity) ?? 0,
                location.MaxWeightKg,
                location.MaxWeightKg.HasValue && location.MaxWeightKg.Value > 0
                    ? Math.Round(((location.Inventories.Sum(inventory => (decimal?)inventory.QuantityOnHand) ?? 0)
                        / location.MaxWeightKg.Value) * 100, 1)
                    : null,
                location.Inventories.Count(inventory => inventory.QuantityOnHand > 0),
                location.Inventories.Count(inventory =>
                    inventory.QuantityOnHand > 0 &&
                    inventory.AvailableQuantity <= (inventory.ProductBatch.CropType.SafetyStockLevelKg ?? 0)),
                location.Inventories.Max(inventory => (DateTime?)inventory.LastUpdatedAt)))
            .ToListAsync(ct);
}
