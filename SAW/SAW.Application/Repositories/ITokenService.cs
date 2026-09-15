using SAW.Application.Features.Authentication.DTOs;

namespace SAW.Application.Repositories;

public interface ITokenService
{
    int RefreshTokenDays { get; }
    AccessTokenResult CreateAccessToken(int accountId, string username, string email, int roleId);
    string CreateOpaqueToken();
    string HashToken(string token);
}
