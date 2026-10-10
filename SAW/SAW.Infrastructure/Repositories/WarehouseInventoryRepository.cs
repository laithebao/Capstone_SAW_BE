using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.WarehouseInventory;
using SAW.Application.Exceptions;
using SAW.Infrastructure.Persistence;
using System.Data;

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
        var items = new List<WarehouseDistributorOrderSummary>(rows.Count);
        foreach (var order in rows)
        {
            var detail = await ToOrderDetail(order, ct);
            items.Add(new WarehouseDistributorOrderSummary(order.PurchaseOrderId, order.OrderCode, order.OrderStatus,
                order.Distributor.DistributorName, order.OrderDetails.Count, order.TotalAmount, order.CreatedAt,
                detail.StockAvailable));
        }
        return new(items, total, page, pageSize);
    }

    public async Task<WarehouseDistributorOrderDetail> GetDistributorOrderAsync(long id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Distributor).Include(o => o.OrderDetails).ThenInclude(d => d.CropType).Include(o => o.OrderDetails).ThenInclude(d => d.RequestedProductBatch).SingleOrDefaultAsync(o => o.PurchaseOrderId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn nhà phân phối.");
        return await ToOrderDetail(order, ct);
    }

    public async Task<WarehouseDistributorOrderDetail> ApproveDistributorOrderAsync(int actorAccountId, long id, CancellationToken ct)
        => await ApproveDistributorOrderAsync(actorAccountId, id, new([]), ct);

    public async Task<WarehouseDistributorOrderDetail> ApproveDistributorOrderAsync(int actorAccountId, long id, WarehouseOrderApprovalRequest request, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var order = await db.PurchaseOrders.Include(o => o.Distributor).Include(o => o.OrderDetails).ThenInclude(d => d.CropType).Include(o => o.OrderDetails).ThenInclude(d => d.RequestedProductBatch).SingleOrDefaultAsync(o => o.PurchaseOrderId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn nhà phân phối.");
        if (order.OrderStatus != "PENDING") throw new ConflictException("Đơn đã được xử lý hoặc đã bị hủy, không thể duyệt lại.");
        if (order.Distributor.HasOverdueBalance) throw new ConflictException("Không thể duyệt đơn của nhà phân phối đang có công nợ quá hạn.");
        var detail = await ToOrderDetail(order, ct);
        if (!detail.StockAvailable) throw new ConflictException("Tồn kho đủ điều kiện hiện tại không đủ để phê duyệt đơn.");
        foreach (var line in order.OrderDetails)
        {
            var input = request.Lines.FirstOrDefault(x => x.OrderDetailId == line.OrderDetailId);
            var approvedWeight = input?.ApprovedWeightKg ?? line.RequestedWeightKg;
            if (approvedWeight <= 0 || approvedWeight > line.RequestedWeightKg) throw new ConflictException("Khối lượng duyệt phải lớn hơn 0 và không vượt quá yêu cầu.");
            var available = detail.Lines.First(x => x.OrderDetailId == line.OrderDetailId).AvailableWeightKg;
            if (approvedWeight > available) throw new ConflictException("Khối lượng duyệt vượt tồn khả dụng.");
            line.ApprovedQuantity = line.RequestedQuantity * approvedWeight / line.RequestedWeightKg;
            line.ApprovedWeightKg = approvedWeight;
            if (input is not null && input.UnitPrice < 0) throw new ConflictException("Đơn giá duyệt không hợp lệ.");
            if (input is not null) line.UnitPrice = input.UnitPrice;
        }
        // Keep the persisted amount check consistent when the manager changes weight or price.
        order.SubtotalAmount = order.OrderDetails.Sum(line => line.ApprovedWeightKg * line.UnitPrice);
        order.TotalAmount = order.SubtotalAmount + order.TaxAmount;
        order.OrderStatus = "APPROVED"; order.ApprovedAt = DateTime.UtcNow; order.ApprovedByAccountId = actorAccountId; order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToOrderDetail(order, ct);
        });
    }

    public async Task<WarehouseDistributorOrderDetail> RejectDistributorOrderAsync(int actorAccountId, long id, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ConflictException("Vui lòng nhập lý do từ chối đơn.");
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var order = await db.PurchaseOrders.Include(o => o.Distributor).Include(o => o.OrderDetails).ThenInclude(d => d.CropType).Include(o => o.OrderDetails).ThenInclude(d => d.RequestedProductBatch).SingleOrDefaultAsync(o => o.PurchaseOrderId == id, ct)
                ?? throw new NotFoundException("Không tìm thấy đơn nhà phân phối.");
            if (order.OrderStatus != "PENDING") throw new ConflictException("Đơn đã được xử lý, không thể từ chối lại.");
            order.OrderStatus = "REJECTED"; order.RejectedAt = DateTime.UtcNow; order.RejectionReason = reason.Trim(); order.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await ToOrderDetail(order, ct);
        });
    }

    private async Task<WarehouseDistributorOrderDetail> ToOrderDetail(SAW.Domain.Entities.PurchaseOrder order, CancellationToken ct)
    {
        var cropIds = order.OrderDetails.Where(d => d.RequestedProductBatchId == null).Select(d => d.CropTypeId).ToList();
        var batchIds = order.OrderDetails.Where(d => d.RequestedProductBatchId != null).Select(d => d.RequestedProductBatchId!.Value).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stock = await db.Inventories.AsNoTracking().Where(i => (batchIds.Contains(i.ProductBatchId) || cropIds.Contains(i.ProductBatch.CropTypeId)) && i.ProductBatch.BatchStatus == "IN_STOCK" && (i.ProductBatch.ExpiryDate == null || i.ProductBatch.ExpiryDate >= today)).GroupBy(i => new { i.ProductBatchId, i.ProductBatch.CropTypeId }).Select(g => new { g.Key.ProductBatchId, g.Key.CropTypeId, Available = g.Sum(i => i.AvailableQuantity) }).ToListAsync(ct);
        var lines = order.OrderDetails.Select(d => { var available = stock.Where(s => d.RequestedProductBatchId.HasValue ? s.ProductBatchId == d.RequestedProductBatchId : s.CropTypeId == d.CropTypeId).Sum(s => s.Available); return new WarehouseDistributorOrderLine(d.OrderDetailId, d.CropType.CropName, d.RequestedProductBatch?.BatchCode, d.RequestedWeightKg, available, d.UnitPrice, d.ApprovedWeightKg, available >= d.RequestedWeightKg); }).ToList();
        return new(order.PurchaseOrderId, order.OrderCode, order.OrderStatus, order.Distributor.DistributorName, order.TotalAmount, order.CreatedAt, order.ExpectedDeliveryDate, order.DeliveryAddress, order.ContactPhone, order.OrderNote, lines.All(l => l.StockAvailable), lines);
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
            var status = !location.MaxWeightKg.HasValue ? "UNCONFIGURED" : location.UtilizationPercent switch
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
                    inventory.ProductBatch.CropType.SafetyStockLevelKg.HasValue &&
                    inventory.AvailableQuantity <= inventory.ProductBatch.CropType.SafetyStockLevelKg.Value),
                location.Inventories.Max(inventory => (DateTime?)inventory.LastUpdatedAt)))
            .ToListAsync(ct);
}
