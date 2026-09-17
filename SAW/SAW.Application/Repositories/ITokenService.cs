using SAW.Application.Features.Authentication.DTOs;

namespace SAW.Application.Repositories;

public interface ITokenService
{
    int RefreshTokenDays { get; }
    AccessTokenResult CreateAccessToken(int accountId, string username, string email, int roleId, string roleCode);
    string CreateOpaqueToken();
    string HashToken(string token);
}
