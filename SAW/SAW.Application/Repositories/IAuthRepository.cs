using SAW.Domain.Entities;

namespace SAW.Application.Repositories;

public interface IAuthRepository
{
    Task<Account?> GetAccountByIdentifierAsync(string identifier, CancellationToken cancellationToken);
    Task<Account?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken);
    Task<Account?> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken);
    Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
    Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken);
    Task<Role?> GetActiveRoleAsync(int roleId, CancellationToken cancellationToken);
    Task CreateRegistrationAsync(Account account, Supplier? supplier, Distributor? distributor, CancellationToken cancellationToken);
    Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);
    Task<RefreshToken?> GetRefreshTokenWithAccountAsync(string tokenHash, CancellationToken cancellationToken);
    Task<IReadOnlyList<PasswordResetToken>> GetActivePasswordResetTokensAsync(int accountId, DateTime now, CancellationToken cancellationToken);
    Task<PasswordResetToken?> GetPasswordResetTokenAsync(string tokenHash, CancellationToken cancellationToken);
    Task<EmailVerificationToken?> GetEmailVerificationTokenAsync(string tokenHash, CancellationToken cancellationToken);
    Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(int accountId, CancellationToken cancellationToken);
    void AddRefreshToken(RefreshToken token);
    void AddPasswordResetToken(PasswordResetToken token);
    void AddEmailVerificationToken(EmailVerificationToken token);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
