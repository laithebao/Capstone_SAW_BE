using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;

namespace SAW.Application.Repositories.Suppliers;

public interface ISupplierRepository
{
    Task<SupplierProfileResponse?> GetProfileByAccountIdAsync(int accountId, CancellationToken cancellationToken = default);
    Task<bool> HasProfileAsync(int accountId, CancellationToken cancellationToken = default);
    Task<bool> IsTaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default);
    Task<string> GenerateSupplierCodeAsync(CancellationToken cancellationToken = default);
    Task AddSupplierAsync(Supplier supplier, List<int> cropTypeIds, List<string> certifications, CancellationToken cancellationToken = default);
    Task<bool> IsTaxCodeExistsExceptCurrentAsync(string taxCode, int currentSupplierId, CancellationToken cancellationToken = default);
    Task UpdateSupplierAsync(Supplier supplier, List<int> cropTypeIds, List<string> certifications, string oldValuesJson, CancellationToken cancellationToken = default);
    Task<Supplier?> GetEntityByAccountIdAsync(int accountId, CancellationToken cancellationToken = default);

}
