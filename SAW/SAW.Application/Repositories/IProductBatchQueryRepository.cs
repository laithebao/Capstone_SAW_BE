using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Domain.Entities;

namespace SAW.Application.Repositories;

public interface IProductBatchQueryRepository
{
    Task<(IReadOnlyList<ProductBatch> Items, int TotalCount)> SearchAsync(
        ProductBatchQuery query, CancellationToken cancellationToken);
    Task<ProductBatchFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken);
    Task<ProductBatch?> GetByIdAsync(long id, CancellationToken cancellationToken);
}
