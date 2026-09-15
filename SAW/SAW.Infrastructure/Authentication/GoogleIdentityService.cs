using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;
using SAW.Application.Repositories;

namespace SAW.Infrastructure.Authentication;

public sealed class GoogleIdentityService(IConfiguration configuration) : IGoogleIdentityService
{
    public async Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        var clientId = configuration["GoogleAuth:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("GoogleAuth:ClientId is required.");

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = [clientId] });
            if (payload.EmailVerified != true || string.IsNullOrWhiteSpace(payload.Email))
                throw new UnauthorizedAccessException("Google email is not verified.");
            return new GoogleIdentity(payload.Subject, payload.Email.ToLowerInvariant(),
                string.IsNullOrWhiteSpace(payload.Name) ? payload.Email : payload.Name);
        }
        catch (InvalidJwtException)
        {
            throw new UnauthorizedAccessException("Google credential is invalid.");
        }
    }
}
