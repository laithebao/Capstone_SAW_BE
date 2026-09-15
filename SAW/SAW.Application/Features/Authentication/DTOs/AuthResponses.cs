namespace SAW.Application.Features.Authentication.DTOs;

public sealed record AuthResponse(
    int AccountId,
    string Username,
    string Email,
    string FullName,
    int RoleId,
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);

public sealed record RegistrationResponse(int AccountId, string Username, string Email, int RoleId);

public sealed record AccessTokenResult(string Token, string JwtId, DateTime ExpiresAt);

public sealed record GoogleLoginResponse(bool RequiresRole, AuthResponse? Session);
