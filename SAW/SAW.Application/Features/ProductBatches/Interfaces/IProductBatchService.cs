using SAW.Application.Features.ProductBatches.Dtos;

namespace SAW.Application.Features.ProductBatches.Interfaces;

public interface IProductBatchService
{
    Task<ProductBatchListResponse> SearchAsync(ProductBatchQuery query, CancellationToken cancellationToken);
    Task<ProductBatchFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken);
    Task<ProductBatchDetail> GetAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductBatchFilterOption>> GetSubmittedSuppliersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SubmittedDeclarationOption>> GetSubmittedBySupplierAsync(int supplierId, CancellationToken cancellationToken);
    Task<SubmittedDeclarationDetail> GetSubmittedDetailAsync(long id, int supplierId, CancellationToken cancellationToken);
    Task<VerifyProductBatchResponse> VerifyAsync(long id, int supplierId, int accountId,
        VerifyProductBatchRequest request, CancellationToken cancellationToken);
}
