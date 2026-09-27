using SAW.Domain.Entities;
using SAW.Application.Features.ProductBatches.Dtos;

namespace SAW.Application.Repositories;

public interface IProductBatchVerificationRepository
{
    Task<bool> ConfirmAsync(long batchId, int supplierId, VerifiedReceivingDetails details,
        BatchStatusHistory history, CancellationToken cancellationToken);
    Task<bool> RejectAsync(long batchId, int supplierId, string reason,
        BatchStatusHistory history, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(long batchId, DateTime? expectedUpdatedAt, DateTime expectedCreatedAt, int accountId,
        VerifiedReceivingDetails details, CancellationToken cancellationToken);
}
