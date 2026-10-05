using SAW.Domain.Entities;

namespace SAW.Application.Repositories;

public sealed record BatchQrSnapshot(ProductBatch Batch, QcInspection? LatestInspection, IReadOnlyList<QrCode> Codes);

public interface IQrCodeRepository
{
    Task<BatchQrSnapshot?> GetAsync(long batchId, CancellationToken ct);
    Task<IReadOnlyList<long>> GetCandidatesAsync(long afterId, int take, CancellationToken ct);
    // Returns the persisted winner, or null when the batch is no longer eligible.
    Task<QrCode?> SaveGeneratedAsync(long batchId, string token, string url, string imageUrl, CancellationToken ct);
    Task<bool> IsImageReferencedAsync(string imageUrl, CancellationToken ct);
}
