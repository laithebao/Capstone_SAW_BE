using SAW.Application.Features.Auth.Login;

namespace SAW.Application.Abstractions.Authentication;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
}
