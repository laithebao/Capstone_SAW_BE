using SAW.Domain.Entities;

namespace SAW.Application.Abstractions.Persistence;

public interface IAccountRepository
{
    Task<Account?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
