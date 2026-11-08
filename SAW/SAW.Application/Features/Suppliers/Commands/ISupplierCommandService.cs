using SAW.Application.Features.Suppliers.DTOs;

namespace SAW.Application.Features.Suppliers.Commands;

public interface ISupplierCommandService
{
    Task<SupplierProfileResponse> GetMyProfileAsync(int currentAccountId, CancellationToken cancellationToken = default);
    Task<SupplierProfileResponse> DeclareProfileAsync(int currentAccountId, DeclareSupplierProfileRequest request, CancellationToken cancellationToken = default);
    Task<SupplierProfileResponse> UpdateProfileAsync(int currentAccountId, UpdateSupplierProfileRequest request, CancellationToken cancellationToken = default);
}