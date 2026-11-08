namespace SAW.Application.Features.Authentication.DTOs;

public sealed record AuthResponse(
    AuthenticatedUser User,
    string AccessToken,
    DateTime ExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);

public sealed record AuthenticatedUser(
    int Id,
    string Name,
    string Email,
    string Role);

public sealed record RegistrationResponse(int AccountId, string Username, string Email, int RoleId);

public sealed record AccessTokenResult(string Token, string JwtId, DateTime ExpiresAt);

public sealed record GoogleLoginResponse(bool RequiresRole, AuthResponse? Session);
