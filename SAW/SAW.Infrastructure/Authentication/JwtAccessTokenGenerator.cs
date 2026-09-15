using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Abstractions.Authentication;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Authentication;

public sealed class JwtAccessTokenGenerator : IAccessTokenGenerator
{
    private readonly IConfiguration _configuration;

    public JwtAccessTokenGenerator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public AccessTokenResult Generate(Account account)
    {
        var issuer = Require("Jwt:Issuer");
        var audience = Require("Jwt:Audience");
        var key = Require("Jwt:Key");
        if (Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");

        var minutes = _configuration.GetValue<int?>("Jwt:AccessTokenMinutes") ?? 30;
        var expiresAt = DateTime.UtcNow.AddMinutes(minutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.AccountId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString()),
            new Claim(ClaimTypes.Name, account.FullName),
            new Claim(ClaimTypes.Email, account.Email),
            new Claim(ClaimTypes.Role, account.Role.RoleCode),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(issuer, audience, claims, expires: expiresAt, signingCredentials: credentials);
        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
    }

    private string Require(string key)
        => _configuration[key] ?? throw new InvalidOperationException($"Configuration '{key}' is required.");
}
