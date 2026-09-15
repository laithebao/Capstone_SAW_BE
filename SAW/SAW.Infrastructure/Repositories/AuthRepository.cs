using Microsoft.EntityFrameworkCore;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class AuthRepository(AppDbContext dbContext) : IAuthRepository
{
    public Task<Account?> GetAccountByIdentifierAsync(string identifier, CancellationToken cancellationToken) =>
        dbContext.Accounts.Include(x => x.Role)
            .SingleOrDefaultAsync(x => x.Username == identifier || x.Email == identifier, cancellationToken);

    public Task<Account?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken) =>
        dbContext.Accounts.Include(x => x.Role)
            .SingleOrDefaultAsync(x => x.Email == email, cancellationToken);

    public Task<Account?> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken) =>
        dbContext.Accounts.SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);

    public Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken) =>
        dbContext.Accounts.AnyAsync(x => x.Username == username, cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        dbContext.Accounts.AnyAsync(x => x.Email == email, cancellationToken);

    public async Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken) =>
        await dbContext.Suppliers.AnyAsync(x => x.TaxCode == taxCode, cancellationToken)
        || await dbContext.Distributors.AnyAsync(x => x.TaxCode == taxCode, cancellationToken);

    public Task<Role?> GetActiveRoleAsync(int roleId, CancellationToken cancellationToken) =>
        dbContext.Roles.SingleOrDefaultAsync(x => x.RoleId == roleId && x.IsActive, cancellationToken);

    public async Task CreateRegistrationAsync(
        Account account, Supplier? supplier, Distributor? distributor, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            dbContext.Accounts.Add(account);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (supplier is not null)
            {
                supplier.AccountId = account.AccountId;
                supplier.SupplierCode = $"SUP-{account.AccountId:D6}";
                dbContext.Suppliers.Add(supplier);
            }
            if (distributor is not null)
            {
                distributor.AccountId = account.AccountId;
                distributor.DistributorCode = $"DST-{account.AccountId:D6}";
                dbContext.Distributors.Add(distributor);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

    public Task<RefreshToken?> GetRefreshTokenWithAccountAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens.Include(x => x.Account).ThenInclude(x => x.Role)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<PasswordResetToken>> GetActivePasswordResetTokensAsync(
        int accountId, DateTime now, CancellationToken cancellationToken) =>
        await dbContext.PasswordResetTokens
            .Where(x => x.AccountId == accountId && x.UsedAt == null && x.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public Task<PasswordResetToken?> GetPasswordResetTokenAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.PasswordResetTokens.Include(x => x.Account)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

    public Task<EmailVerificationToken?> GetEmailVerificationTokenAsync(
        string tokenHash, CancellationToken cancellationToken) =>
        dbContext.EmailVerificationTokens.Include(x => x.Account)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(
        int accountId, CancellationToken cancellationToken) =>
        await dbContext.RefreshTokens.Where(x => x.AccountId == accountId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

    public void AddRefreshToken(RefreshToken token) => dbContext.RefreshTokens.Add(token);
    public void AddPasswordResetToken(PasswordResetToken token) => dbContext.PasswordResetTokens.Add(token);
    public void AddEmailVerificationToken(EmailVerificationToken token) => dbContext.EmailVerificationTokens.Add(token);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
