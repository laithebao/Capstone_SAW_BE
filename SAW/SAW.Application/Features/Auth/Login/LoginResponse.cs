namespace SAW.Application.Features.Auth.Login;

public sealed record LoginResponse(
    string AccessToken,
    DateTime ExpiresAt,
    AuthenticatedUser User);

public sealed record AuthenticatedUser(
    int Id,
    string Name,
    string Email,
    string Role);
