using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class ProductBatchQueryRepository(AppDbContext dbContext) : IProductBatchQueryRepository
{
    private IQueryable<ProductBatch> WarehouseBatches() =>
        dbContext.ProductBatches.AsNoTracking().Where(b =>
            b.BatchStatus != "SUBMITTED" &&
            b.BatchStatus != "PENDING_PREDECLARATION" &&
            b.BatchStatus != "PENDING_APPROVAL" &&
            b.BatchStatus != "CANCELLED");

    public async Task<IReadOnlyList<ProductBatchFilterOption>> GetSubmittedSuppliersAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Suppliers.AsNoTracking()
            .Where(s => s.ProductBatches.Any(b => b.BatchStatus == "SUBMITTED" &&
                b.VerifiedQuantity == null && b.VerifiedWeightInKg == null))
            .OrderBy(s => s.SupplierName)
            .Select(s => new ProductBatchFilterOption(s.SupplierId, s.SupplierName))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SubmittedDeclarationOption>> GetSubmittedBySupplierAsync(
        int supplierId, CancellationToken cancellationToken) =>
        await dbContext.ProductBatches.AsNoTracking()
            .Where(b => b.SupplierId == supplierId && b.BatchStatus == "SUBMITTED" &&
                b.VerifiedQuantity == null && b.VerifiedWeightInKg == null)
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.ProductBatchId)
            .Select(b => new SubmittedDeclarationOption(
                b.ProductBatchId, b.BatchCode, b.ProductName, b.DeclaredQuantity, b.Unit))
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<ProductBatch> Items, int TotalCount)> SearchAsync(
        ProductBatchQuery filter, CancellationToken cancellationToken)
    {
        var query = WarehouseBatches();
        if (!string.IsNullOrWhiteSpace(filter.BatchCode))
            query = query.Where(x => x.BatchCode.Contains(filter.BatchCode));
        if (filter.SupplierId.HasValue)
            query = query.Where(x => x.SupplierId == filter.SupplierId.Value);
        if (filter.CropTypeId.HasValue)
            query = query.Where(x => x.CropTypeId == filter.CropTypeId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(x => x.BatchStatus == filter.Status);
        if (filter.WithoutActiveInspection)
            query = query.Where(x => !x.QcInspections.Any(q => q.InspectionStatus != "COMPLETED"));

        var total = await query.CountAsync(cancellationToken);
        query = filter.SortBy switch
        {
            "createdAtAsc" => query.OrderBy(x => x.CreatedAt).ThenBy(x => x.ProductBatchId),
            "updatedAtDesc" => query.OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt).ThenByDescending(x => x.ProductBatchId),
            "updatedAtAsc" => query.OrderBy(x => x.UpdatedAt ?? x.CreatedAt).ThenBy(x => x.ProductBatchId),
            _ => query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.ProductBatchId)
        };
        var items = await query
            .Include(x => x.Supplier)
            .Include(x => x.CropType)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<ProductBatchFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken)
    {
        var suppliers = await WarehouseBatches()
            .Select(b => new { b.SupplierId, b.Supplier.SupplierName })
            .Distinct()
            .OrderBy(x => x.SupplierName)
            .Select(x => new ProductBatchFilterOption(x.SupplierId, x.SupplierName))
            .ToListAsync(cancellationToken);
        var cropTypes = await WarehouseBatches()
            .Select(b => new { b.CropTypeId, b.CropType.CropName })
            .Distinct()
            .OrderBy(x => x.CropName)
            .Select(x => new ProductBatchFilterOption(x.CropTypeId, x.CropName))
            .ToListAsync(cancellationToken);
        var statuses = await WarehouseBatches()
            .Select(x => x.BatchStatus).Distinct().OrderBy(x => x)
            .ToListAsync(cancellationToken);
        return new ProductBatchFilterOptions(suppliers, cropTypes, statuses);
    }

    public Task<ProductBatch?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
        dbContext.ProductBatches.AsNoTracking()
            .Include(x => x.Supplier)
            .Include(x => x.CropType)
            .Include(x => x.GrowingArea)
            .SingleOrDefaultAsync(x => x.ProductBatchId == id, cancellationToken);

    public Task<bool> HasQcInspectionAsync(long id, CancellationToken cancellationToken) =>
        dbContext.QcInspections.AnyAsync(q => q.ProductBatchId == id, cancellationToken);

    public async Task<ProductBatchRelatedInformation> GetRelatedInformationAsync(long id, CancellationToken ct)
    {
        // Same latest-inspection ordering as Goods Receipt, QR and public traceability.
        // Never filter by result/status first: a newer draft must supersede an older PASS.
        var qc = await dbContext.QcInspections.AsNoTracking().Where(q => q.ProductBatchId == id)
            .OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId)
            .Select(q => new ProductBatchQcSummary(q.QcInspectionId, q.InspectionCode,
                q.InspectionStatus, q.QcResult, q.QualityGrade, q.StartedAt, q.CompletedAt,
                q.QcAccount.FullName, q.Note)).FirstOrDefaultAsync(ct);
        var receipts = await dbContext.GoodsReceipts.AsNoTracking().Where(r => r.ProductBatchId == id)
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.GoodsReceiptId)
            .Select(r => new ProductBatchGoodsReceipt(r.GoodsReceiptId, r.ReceiptCode, r.ReceiptStatus,
                r.ReceivedAt, r.CommittedAt, r.WarehouseLocationId,
                r.WarehouseLocation.LocationCode + " · " + r.WarehouseLocation.ZoneName + " / "
                    + (r.WarehouseLocation.RackName ?? "—") + " / " + (r.WarehouseLocation.BinName ?? "—"),
                r.OperationAccount.FullName, r.Note, r.ReceivedQuantity, r.Unit, r.WeightInKg)).ToListAsync(ct);
        var history = await dbContext.BatchStatusHistories.AsNoTracking().Where(h => h.ProductBatchId == id)
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.BatchStatusHistoryId)
            .Select(h => new ProductBatchStatusHistoryItem(h.BatchStatusHistoryId, h.ChangedAt,
                h.OldStatus, h.NewStatus, h.ChangedByAccount == null ? null : h.ChangedByAccount.FullName,
                h.ChangeReason)).ToListAsync(ct);

        var transactions = receipts.Select(r => new ProductBatchWarehouseTransaction(r.Id,
            "GOODS_RECEIPT", r.ReceiptCode, r.ReceiptStatus, r.CommittedAt ?? r.ReceivedAt,
            r.ReceivedQuantity, r.Unit, r.WeightInKg, null, r.LocationName,
            r.OperationStaffName, r.Note)).ToList();
        // Read quantities from the stored documents, never from current inventory or batch values.
        transactions.AddRange(await dbContext.GoodsIssueDetails.AsNoTracking()
            .Where(d => d.Inventory.ProductBatchId == id)
            .Select(d => new ProductBatchWarehouseTransaction(d.GoodsIssueDetailId, "GOODS_ISSUE",
                d.GoodsIssue.IssueCode, dbContext.GoodsIssueInvalidations.Any(v => v.GoodsIssueId == d.GoodsIssueId) ? "CANCELLED" : d.GoodsIssue.IssueStatus, d.GoodsIssue.CommittedAt ?? d.GoodsIssue.IssuedAt,
                d.IssuedQuantity, d.Unit, d.WeightInKg, d.Inventory.WarehouseLocation.LocationCode,
                null, d.GoodsIssue.OperationAccount.FullName, d.GoodsIssue.Note)).ToListAsync(ct));
        transactions.AddRange(await dbContext.StockTransfers.AsNoTracking().Where(t => t.ProductBatchId == id)
            .Select(t => new ProductBatchWarehouseTransaction(t.StockTransferId, "STOCK_TRANSFER",
                t.TransferCode, t.TransferStatus, t.TransferredAt, t.Quantity, t.Unit, null,
                t.FromWarehouseLocation.LocationCode, t.ToWarehouseLocation.LocationCode,
                t.OperationAccount.FullName, t.Note)).ToListAsync(ct));
        transactions.AddRange(await dbContext.StockAdjustments.AsNoTracking()
            .Where(a => a.Inventory.ProductBatchId == id)
            .Select(a => new ProductBatchWarehouseTransaction(a.StockAdjustmentId, a.RequestKind == "DISPOSAL" ? "STOCK_DISPOSAL" : "STOCK_ADJUSTMENT",
                a.AdjustmentCode, a.AdjustmentStatus, a.ReviewedAt ?? a.CreatedAt, a.DifferenceQuantity, a.Inventory.Unit,
                null, a.RequestKind == "DISPOSAL" ? "Toàn bộ vị trí của lô" : a.Inventory.WarehouseLocation.LocationCode,
                null, a.ReviewedByAccount == null ? a.CreatedByAccount.FullName : a.ReviewedByAccount.FullName,
                a.ReviewNote == null ? a.Reason : a.Reason + " · Xử lý: " + a.ReviewNote)).ToListAsync(ct));
        return new(qc, receipts, history, transactions.OrderByDescending(t => t.RecordedAt)
            .ThenBy(t => t.TransactionType, StringComparer.Ordinal).ThenByDescending(t => t.Id).ToList());
    }

    public Task<ProductBatch?> GetWarehouseByIdAsync(long id, CancellationToken cancellationToken) =>
        WarehouseBatches()
            .Include(x => x.Supplier)
            .Include(x => x.CropType)
            .Include(x => x.GrowingArea)
            .SingleOrDefaultAsync(x => x.ProductBatchId == id, cancellationToken);
}
