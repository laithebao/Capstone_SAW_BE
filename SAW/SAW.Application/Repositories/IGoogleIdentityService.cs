namespace SAW.Application.Repositories;

public sealed record GoogleIdentity(string Subject, string Email, string FullName);

public interface IGoogleIdentityService
{
    Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken cancellationToken);
}
