using System.Text.RegularExpressions;
using SAW.Application.Exceptions;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Interfaces;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Application.Features.ProductBatches.Services;

public sealed class ProductBatchService(IProductBatchQueryRepository repository) : IProductBatchService
{
    private static readonly HashSet<string> AllowedSorts =
        ["createdAtDesc", "createdAtAsc", "updatedAtDesc", "updatedAtAsc"];

    public async Task<ProductBatchListResponse> SearchAsync(ProductBatchQuery query, CancellationToken cancellationToken)
    {
        var batchCode = query.BatchCode?.Trim();
        var status = query.Status?.Trim().ToUpperInvariant();
        var sortBy = string.IsNullOrWhiteSpace(query.SortBy) ? "createdAtDesc" : query.SortBy.Trim();

        if (query.Page < 1 || query.PageSize is < 1 or > 100)
            throw new BadRequestException("Invalid page number or page size.");
        if (query.SupplierId is <= 0 || query.CropTypeId is <= 0 ||
            (batchCode is not null && (batchCode.Length > 50 || batchCode.Any(char.IsControl))) ||
            (status is not null && (status.Length > 40 || !Regex.IsMatch(status, "^[A-Z0-9_]*$"))))
            throw new BadRequestException("Invalid search or filter value.");
        if (!AllowedSorts.Contains(sortBy))
            throw new BadRequestException("Invalid sort value.");

        var normalized = query with
        {
            BatchCode = string.IsNullOrWhiteSpace(batchCode) ? null : batchCode,
            Status = string.IsNullOrWhiteSpace(status) ? null : status,
            SortBy = sortBy
        };
        var result = await repository.SearchAsync(normalized, cancellationToken);
        return new ProductBatchListResponse(result.Items.Select(MapList).ToList(),
            result.TotalCount, normalized.Page, normalized.PageSize);
    }

    public Task<ProductBatchFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken) =>
        repository.GetFilterOptionsAsync(cancellationToken);

    public async Task<ProductBatchDetail> GetAsync(long id, CancellationToken cancellationToken)
    {
        var batch = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Product batch not found.");
        return new ProductBatchDetail(
            batch.ProductBatchId, batch.BatchCode, batch.ProductName,
            batch.SupplierId, batch.Supplier.SupplierName,
            batch.CropTypeId, batch.CropType.CropName, batch.CropType.CategoryName,
            batch.DeclaredQuantity, batch.Unit, batch.HarvestDate,
            batch.GrowingArea.AreaName, batch.ExpectedDeliveryDate, batch.ExpiryDate,
            batch.CreatedAt, batch.UpdatedAt, batch.BatchStatus);
    }

    private static ProductBatchListItem MapList(ProductBatch batch) => new(
        batch.ProductBatchId, batch.BatchCode, batch.ProductName,
        batch.SupplierId, batch.Supplier.SupplierName,
        batch.CropTypeId, batch.CropType.CropName, batch.CropType.CategoryName,
        batch.DeclaredQuantity, batch.Unit, batch.CreatedAt, batch.UpdatedAt,
        batch.BatchStatus);
}
