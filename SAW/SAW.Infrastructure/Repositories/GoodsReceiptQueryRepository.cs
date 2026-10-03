using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class GoodsReceiptQueryRepository(AppDbContext db) : IGoodsReceiptQueryRepository
{
    private static readonly Expression<Func<GoodsReceipt, GoodsReceiptDetail>> Projection = r => new(
        r.GoodsReceiptId, r.ReceiptCode, r.ProductBatchId, r.ProductBatch.BatchCode,
        r.ProductBatch.ProductName, r.ProductBatch.SupplierId, r.ProductBatch.Supplier.SupplierName,
        r.ReceivedQuantity, r.Unit, r.WeightInKg, r.WarehouseLocationId,
        r.WarehouseLocation.LocationCode + " · " + r.WarehouseLocation.ZoneName + " / " + (r.WarehouseLocation.RackName ?? "—") + " / " + (r.WarehouseLocation.BinName ?? "—"),
        DateOnly.FromDateTime(r.ReceivedAt), r.ReceiptStatus, r.CommittedAt, r.Note, r.OperationAccount.FullName,
        new GoodsReceiptSnapshot(r.WarehouseLocationId, r.ReceivedAt, r.Note));

    public async Task<GoodsReceiptPage<GoodsReceiptDetail>> SearchAsync(GoodsReceiptQuery query, CancellationToken ct)
    {
        var rows = db.GoodsReceipts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search)) rows = rows.Where(r => r.ReceiptCode.Contains(query.Search) || r.ProductBatch.BatchCode.Contains(query.Search));
        if (query.SupplierId.HasValue) rows = rows.Where(r => r.ProductBatch.SupplierId == query.SupplierId);
        if (query.WarehouseLocationId.HasValue) rows = rows.Where(r => r.WarehouseLocationId == query.WarehouseLocationId);
        if (!string.IsNullOrEmpty(query.Status)) rows = rows.Where(r => r.ReceiptStatus == query.Status);
        if (query.FromDate.HasValue) { var from = query.FromDate.Value.ToDateTime(TimeOnly.MinValue); rows = rows.Where(r => r.ReceivedAt >= from); }
        if (query.ToDate.HasValue) { var until = query.ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue); rows = rows.Where(r => r.ReceivedAt < until); }
        var total = await rows.CountAsync(ct);
        var ordered = query.SortBy switch
        {
            "receivedAtAsc" => rows.OrderBy(r => r.ReceivedAt).ThenBy(r => r.GoodsReceiptId),
            "idDesc" => rows.OrderByDescending(r => r.GoodsReceiptId),
            "idAsc" => rows.OrderBy(r => r.GoodsReceiptId),
            _ => rows.OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.GoodsReceiptId)
        };
        var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(Projection).ToListAsync(ct);
        return new(items, total, query.Page, query.PageSize);
    }

    public Task<GoodsReceiptDetail?> GetAsync(long id, CancellationToken ct) => db.GoodsReceipts.AsNoTracking()
        .Where(r => r.GoodsReceiptId == id).Select(Projection).SingleOrDefaultAsync(ct);

    public async Task<GoodsReceiptFilters> FiltersAsync(CancellationToken ct) => new(
        await db.Suppliers.AsNoTracking().OrderBy(s => s.SupplierName).Select(s => new GoodsReceiptOption(s.SupplierId, s.SupplierName)).ToListAsync(ct),
        await db.WarehouseLocations.AsNoTracking().OrderBy(l => l.LocationCode).Select(l => new GoodsReceiptLocation(
            l.WarehouseLocationId, l.LocationCode + " · " + l.ZoneName + " / " + (l.RackName ?? "—") + " / " + (l.BinName ?? "—"),
            l.LocationStatus == "ACTIVE", l.MaxWeightKg)).ToListAsync(ct));

    public async Task<GoodsReceiptPage<GoodsReceiptBatch>> EligibleAsync(int? supplierId, string? search, int page, int pageSize, CancellationToken ct)
    {
        var rows = db.ProductBatches.AsNoTracking().Where(b => b.BatchStatus == "APPROVED_FOR_STORAGE"
            && b.VerifiedQuantity > 0 && b.VerifiedWeightInKg > 0 && b.Unit.Trim() != ""
            && !b.GoodsReceipts.Any(r => r.ReceiptStatus == "DRAFT" || r.ReceiptStatus == "COMMITTED")
            && b.QcInspections.OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId).Take(1)
                .Any(q => q.InspectionStatus == "COMPLETED" && q.CompletedAt != null && q.QcResult == "PASS"
                    && (q.QualityGrade == "A" || q.QualityGrade == "B" || q.QualityGrade == "C" || q.QualityGrade == "D") && q.QualityGrade == b.QualityGrade));
        if (supplierId.HasValue) rows = rows.Where(b => b.SupplierId == supplierId);
        if (!string.IsNullOrEmpty(search)) rows = rows.Where(b => b.BatchCode.Contains(search) || b.ProductName.Contains(search));
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(b => b.ProductBatchId).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new GoodsReceiptBatch(b.ProductBatchId, b.BatchCode, b.ProductName, b.SupplierId,
                b.Supplier.SupplierName, b.VerifiedQuantity!.Value, b.Unit, b.VerifiedWeightInKg!.Value, b.QualityGrade!)).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }
}
