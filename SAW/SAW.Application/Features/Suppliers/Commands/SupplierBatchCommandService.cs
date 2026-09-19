using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;

namespace SAW.Application.Features.Suppliers.Commands;

public class SupplierBatchCommandService : ISupplierBatchCommandService
{
    private readonly IProductBatchRepository _productBatchRepository;

    public SupplierBatchCommandService(IProductBatchRepository productBatchRepository)
    {
        _productBatchRepository = productBatchRepository;
    }

    public async Task<SupplierBatchListResponse> GetDeclaredBatchesAsync(int currentAccountId, GetSupplierBatchesQueryRequest request, CancellationToken cancellationToken = default)
    {
        return await _productBatchRepository.GetBatchesBySupplierAccountIdAsync(currentAccountId, request, cancellationToken);
    }
}
