using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;

namespace SAW.Application.Repositories.Suppliers;

public interface IProductBatchRepository
{
    Task<SupplierBatchListResponse> GetBatchesBySupplierAccountIdAsync(int accountId, GetSupplierBatchesQueryRequest request, CancellationToken cancellationToken = default);
}
