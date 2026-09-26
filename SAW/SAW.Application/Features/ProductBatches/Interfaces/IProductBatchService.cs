using SAW.Application.Features.ProductBatches.Dtos;

namespace SAW.Application.Features.ProductBatches.Interfaces;

public interface IProductBatchService
{
    Task<ProductBatchListResponse> SearchAsync(ProductBatchQuery query, CancellationToken cancellationToken);
    Task<ProductBatchFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken);
    Task<ProductBatchDetail> GetAsync(long id, CancellationToken cancellationToken);
}
