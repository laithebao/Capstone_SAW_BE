using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class ProductBatchQueryRepository(AppDbContext dbContext) : IProductBatchQueryRepository
{
    public async Task<(IReadOnlyList<ProductBatch> Items, int TotalCount)> SearchAsync(
        ProductBatchQuery filter, CancellationToken cancellationToken)
    {
        var query = dbContext.ProductBatches.AsNoTracking().AsQueryable();
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
        var suppliers = await dbContext.Suppliers.AsNoTracking()
            .Where(x => x.ProductBatches.Any())
            .OrderBy(x => x.SupplierName)
            .Select(x => new ProductBatchFilterOption(x.SupplierId, x.SupplierName))
            .ToListAsync(cancellationToken);
        var cropTypes = await dbContext.CropTypes.AsNoTracking()
            .Where(x => x.ProductBatches.Any())
            .OrderBy(x => x.CropName)
            .Select(x => new ProductBatchFilterOption(x.CropTypeId, x.CropName))
            .ToListAsync(cancellationToken);
        var statuses = await dbContext.ProductBatches.AsNoTracking()
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
}
