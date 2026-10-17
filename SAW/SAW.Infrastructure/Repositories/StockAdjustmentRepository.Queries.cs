using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    private static IQueryable<StockAdjustment> Details(AppDbContext db) => db.StockAdjustments.AsNoTracking()
        .Include(a => a.Inventory).ThenInclude(i => i.ProductBatch).Include(a => a.Inventory).ThenInclude(i => i.WarehouseLocation)
        .Include(a => a.CreatedByAccount).Include(a => a.ReviewedByAccount).Include(a => a.Lines).ThenInclude(l => l.Inventory)
        .Include(a => a.Images).AsSplitQuery();
    private static AdjustmentSummary Summary(StockAdjustment a) => new(a.StockAdjustmentId, a.AdjustmentCode, a.RequestKind,
        a.AdjustmentType, a.Inventory.ProductBatchId, a.Inventory.ProductBatch.BatchCode, a.Inventory.ProductBatch.ProductName,
        a.Lines.Count == 0 ? a.Inventory.WarehouseLocation.LocationCode : string.Join(", ", a.Lines.OrderBy(l => l.InventoryId).Select(l => l.LocationCode)),
        a.SystemQuantity, a.ActualQuantity, a.DifferenceQuantity, a.AdjustmentStatus, a.CreatedByAccount.FullName, a.CreatedAt);
    public async Task<AdjustmentDetail> GetAsync(long id, CancellationToken ct)
    {
        await using var db = Context();
        return await ReadDetailAsync(db, id, ct);
    }
    private static async Task<AdjustmentDetail> ReadDetailAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var a = await Details(db).SingleOrDefaultAsync(a => a.StockAdjustmentId == id, ct) ?? throw new NotFoundException("Không tìm thấy phiếu điều chỉnh.");
        // Legacy slips have no line snapshots; retain their saved quantities and identify unavailable snapshots as NULL.
        var lines = a.Lines.Count == 0
            ? new List<AdjustmentLine> { new(a.InventoryId, a.Inventory.WarehouseLocationId, a.Inventory.WarehouseLocation.LocationCode,
                a.SystemQuantity, a.ActualQuantity, a.DifferenceQuantity, null, null) }
            : a.Lines.OrderBy(l => l.InventoryId).Select(l => new AdjustmentLine(l.InventoryId, l.Inventory.WarehouseLocationId,
                l.LocationCode, l.SystemQuantity, l.ActualQuantity, l.ActualQuantity - l.SystemQuantity, l.ReservedQuantity, l.InventoryUpdatedAt)).ToList();
        return new(Summary(a), a.Reason, a.ReviewedByAccount?.FullName, a.ReviewedAt, a.ReviewNote, lines,
            a.Images.OrderBy(i => i.UploadedAt).ThenBy(i => i.StockAdjustmentImageId).Select(i => new AdjustmentImage(i.StockAdjustmentImageId, i.ImageUrl)).ToList());
    }
    public async Task<AdjustmentPage<AdjustmentSummary>> SearchAsync(StockAdjustmentQuery q, CancellationToken ct)
    {
        await using var db = Context(); var rows = db.StockAdjustments.AsNoTracking().AsQueryable();
        if (q.Search is { } term) rows = rows.Where(a => a.AdjustmentCode.Contains(term) || a.Inventory.ProductBatch.BatchCode.Contains(term)
            || a.Inventory.ProductBatch.ProductName.Contains(term) || a.Reason.Contains(term));
        if (q.ProductBatchId is { } batch) rows = rows.Where(a => a.Inventory.ProductBatchId == batch);
        if (q.WarehouseLocationId is { } location) rows = rows.Where(a => a.Inventory.WarehouseLocationId == location || a.Lines.Any(l => l.Inventory.WarehouseLocationId == location));
        if (q.RequestKind is { } kind) rows = rows.Where(a => a.RequestKind == kind);
        if (q.AdjustmentType is { } type) rows = rows.Where(a => a.AdjustmentType == type);
        if (q.Status is { } status) rows = rows.Where(a => a.AdjustmentStatus == status);
        if (q.FromDate is { } from) { var date = from.ToDateTime(TimeOnly.MinValue).AddHours(-7); rows = rows.Where(a => a.CreatedAt >= date); }
        if (q.ToDate is { } to) { var date = to.AddDays(1).ToDateTime(TimeOnly.MinValue).AddHours(-7); rows = rows.Where(a => a.CreatedAt < date); }
        var count = await rows.CountAsync(ct);
        var ordered = q.SortBy switch { "createdAsc" => rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.StockAdjustmentId),
            "codeAsc" => rows.OrderBy(a => a.AdjustmentCode).ThenBy(a => a.StockAdjustmentId),
            "codeDesc" => rows.OrderByDescending(a => a.AdjustmentCode).ThenByDescending(a => a.StockAdjustmentId),
            _ => rows.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.StockAdjustmentId) };
        var ids = await ordered.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(a => a.StockAdjustmentId).ToListAsync(ct);
        var data = await Details(db).Where(a => ids.Contains(a.StockAdjustmentId)).ToListAsync(ct);
        var byId = data.ToDictionary(a => a.StockAdjustmentId);
        return new(ids.Select(id => Summary(byId[id])).ToList(), count, q.Page, q.PageSize);
    }
    public async Task<AdjustmentPage<AdjustmentBatch>> BatchesAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        await using var db = Context();
        // Include expired/quarantined/damaged stock: this is a handling lookup, not a sale catalog.
        var rows = db.ProductBatches.AsNoTracking().Where(b => b.Inventories.Any());
        if (search is { } term) rows = rows.Where(b => b.BatchCode.Contains(term) || b.ProductName.Contains(term));
        var count = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(b => b.ProductBatchId).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new AdjustmentBatch(b.ProductBatchId, b.BatchCode, b.ProductName, b.ExpiryDate, b.Inventories.Sum(i => i.QuantityOnHand))).ToListAsync(ct);
        return new(items, count, page, pageSize);
    }
    public async Task<IReadOnlyList<AdjustmentLocation>> LocationsAsync(CancellationToken ct)
    {
        await using var db = Context();
        return await db.WarehouseLocations.AsNoTracking().OrderBy(l => l.LocationCode).Select(l => new AdjustmentLocation(l.WarehouseLocationId, l.LocationCode)).ToListAsync(ct);
    }
}
