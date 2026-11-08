using SAW.Application.Features.QrCodes.Dtos;

namespace SAW.Application.Features.QrCodes.Interfaces;

public interface IProductBatchQrCodeService
{
    Task<ProductBatchQrCodeResponse> GetAsync(long batchId, CancellationToken ct);
    Task GenerateAsync(long batchId, CancellationToken ct);
}
