using Microsoft.EntityFrameworkCore;
using SAW.Application.Abstractions.Persistence;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class AccountRepository : IAccountRepository
{
    private readonly AppDbContext _dbContext;

    public AccountRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Account?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken)
    {
        return _dbContext.Accounts
            .Include(x => x.Role)
            .SingleOrDefaultAsync(
                x => x.Email.ToLower() == identifier || x.Username.ToLower() == identifier,
                cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
        => _dbContext.SaveChangesAsync(cancellationToken);
}
