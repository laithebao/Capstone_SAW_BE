using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;

namespace SAW.Application.Features.Suppliers.Commands;

public interface ISupplierBatchCommandService
{
    Task<SupplierBatchListResponse> GetDeclaredBatchesAsync(int currentAccountId, GetSupplierBatchesQueryRequest request, CancellationToken cancellationToken = default);
    Task<SupplierBatchItemResponse> DeclareBatchAsync(int currentAccountId, DeclareProductBatchRequest request, CancellationToken cancellationToken = default);
    Task<SupplierBatchItemResponse> UpdateDeclaredBatchAsync(long batchId, int currentAccountId, UpdateProductBatchRequest request, CancellationToken cancellationToken = default);
    Task<SupplierBatchStatusResponse> GetBatchStatusDetailAsync(long batchId, int currentAccountId, CancellationToken cancellationToken = default);

}
