using SAW.Application.Features.Authentication.DTOs;

namespace SAW.Application.Features.Authentication.Commands;

public interface IAuthCommandService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken);
    Task<GoogleLoginResponse> GoogleLoginAsync(GoogleLoginRequest request, string? ipAddress, CancellationToken cancellationToken);
    Task<AuthResponse> RefreshTokenAsync(RefreshTokenRequest request, string? ipAddress, CancellationToken cancellationToken);
    Task<RegistrationResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken);
    Task LogoutAsync(LogoutRequest request, string? ipAddress, CancellationToken cancellationToken);
    Task RequestPasswordResetAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
    Task ChangePasswordAsync(int accountId, ChangePasswordRequest request, CancellationToken cancellationToken);
}
