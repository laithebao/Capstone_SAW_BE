using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;

namespace SAW.Application.Repositories.Suppliers;

public interface IProductBatchRepository
{
    Task<SupplierBatchListResponse> GetBatchesBySupplierAccountIdAsync(int accountId, GetSupplierBatchesQueryRequest request, CancellationToken cancellationToken = default);
    Task<bool> IsCropTypeRegisteredForSupplierAsync(int supplierId, int cropTypeId, CancellationToken cancellationToken = default);
    Task<bool> IsGrowingAreaRegisteredForSupplierAsync(int supplierId, int areaId, CancellationToken cancellationToken = default);
    Task<List<SupplierDocumentDto>> GetDocumentsAsync(long batchId, CancellationToken cancellationToken = default);
    Task<string> GenerateBatchCodeAsync(CancellationToken cancellationToken = default);
    Task AddProductBatchAsync(ProductBatch batch, BatchStatusHistory initialHistory, List<string>? documentUrls, CancellationToken cancellationToken = default);
    Task<ProductBatch?> GetBatchByIdAsync(long batchId, CancellationToken cancellationToken = default);
    Task UpdateProductBatchAsync(ProductBatch batch, BatchStatusHistory statusHistory, DateTime expectedCreatedAt, DateTime? expectedUpdatedAt, List<string>? documentUrls, CancellationToken cancellationToken = default);
    Task<ProductBatch?> GetBatchStatusDetailByIdAsync(long batchId, CancellationToken cancellationToken = default);
    Task<decimal> GetCommittedReceivedQuantityAsync(long batchId, CancellationToken cancellationToken = default);
    Task<List<BatchStatusHistory>> GetBatchStatusHistoryAsync(long batchId, CancellationToken cancellationToken = default);

}
