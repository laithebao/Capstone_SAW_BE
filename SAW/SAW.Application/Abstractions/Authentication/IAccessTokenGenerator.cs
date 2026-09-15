using SAW.Domain.Entities;

namespace SAW.Application.Abstractions.Authentication;

public interface IAccessTokenGenerator
{
    AccessTokenResult Generate(Account account);
}

public sealed record AccessTokenResult(string Token, DateTime ExpiresAt);
