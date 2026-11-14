using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.UserProfiles;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class UserProfileRepository(AppDbContext dbContext) : IUserProfileRepository
{
    public Task<Account?> GetByAccountIdAsync(int accountId, CancellationToken cancellationToken) =>
        dbContext.Accounts.Include(x => x.Role).Include(x => x.Distributor)
            .SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);

    public async Task<bool> TaxCodeExistsAsync(string taxCode, int excludingDistributorId, CancellationToken cancellationToken) =>
        await dbContext.Distributors.AnyAsync(x => x.TaxCode == taxCode && x.DistributorId != excludingDistributorId, cancellationToken)
        || await dbContext.Suppliers.AnyAsync(x => x.TaxCode == taxCode, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new ConflictException("Mã số thuế đã được sử dụng. Vui lòng kiểm tra lại.");
        }
    }
}
