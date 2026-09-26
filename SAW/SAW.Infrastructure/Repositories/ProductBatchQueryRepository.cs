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

    public Task<ProductBatch?> GetWarehouseByIdAsync(long id, CancellationToken cancellationToken) =>
        WarehouseBatches()
            .Include(x => x.Supplier)
            .Include(x => x.CropType)
            .Include(x => x.GrowingArea)
            .SingleOrDefaultAsync(x => x.ProductBatchId == id, cancellationToken);
}
