using SAW.Domain.Entities;

namespace SAW.Application.Repositories;

public interface IProductBatchVerificationRepository
{
    Task<bool> ConfirmAsync(long batchId, int supplierId, decimal verifiedQuantity,
        decimal verifiedWeightInKg, BatchStatusHistory history, CancellationToken cancellationToken);
}
