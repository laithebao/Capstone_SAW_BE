using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;
using static SAW.Application.Features.Suppliers.DTOs.DeclareSupplierProfileRequest;

namespace SAW.Application.Features.Suppliers.Commands;

public interface ISupplierCommandService
{
    Task<SupplierProfileResponse> GetMyProfileAsync(int currentAccountId, CancellationToken cancellationToken = default);
    Task<SupplierProfileResponse> DeclareProfileAsync(int currentAccountId, DeclareSupplierProfileRequest request, CancellationToken cancellationToken = default);
    Task<SupplierProfileResponse> UpdateProfileAsync(int currentAccountId, UpdateSupplierProfileRequest request, CancellationToken cancellationToken = default);


}
