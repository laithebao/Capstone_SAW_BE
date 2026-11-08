using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Features.Authentication.DTOs;
using SAW.Application.Repositories;

namespace SAW.Infrastructure.Authentication;

public sealed class JwtTokenService : ITokenService
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly byte[] _key;
    private readonly int _accessTokenMinutes;
    public int RefreshTokenDays { get; }

    public JwtTokenService(IConfiguration configuration)
    {
        _issuer = configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is missing.");
        _audience = configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience is missing.");
        var secret = configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing.");
        if (Encoding.UTF8.GetByteCount(secret) < 32) throw new InvalidOperationException("Jwt:Key must be at least 32 bytes.");
        _key = Encoding.UTF8.GetBytes(secret);
        _accessTokenMinutes = configuration.GetValue("Jwt:AccessTokenMinutes", 30);
        RefreshTokenDays = configuration.GetValue("Jwt:RefreshTokenDays", 7);
    }

    public AccessTokenResult CreateAccessToken(int accountId, string username, string email, int roleId, string roleCode)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(_accessTokenMinutes);
        var jwtId = Guid.NewGuid().ToString("N");
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, accountId.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, username),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, jwtId),
            new Claim("account_id", accountId.ToString()),
            new Claim("name", username),
            new Claim("role_id", roleId.ToString()),
            new Claim(ClaimTypes.Role, roleCode)
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_issuer, _audience, claims, now, expiresAt, credentials);
        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), jwtId, expiresAt);
    }

    public string CreateOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
