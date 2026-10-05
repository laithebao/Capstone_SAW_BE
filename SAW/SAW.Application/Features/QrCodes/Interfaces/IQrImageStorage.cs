namespace SAW.Application.Features.QrCodes.Interfaces;

public interface IQrImageStorage
{
    bool IsConfigured { get; }
    // Caller chooses a unique asset ID so uncertain uploads can also be cleaned up.
    Task<string> UploadAsync(string assetId, byte[] png, CancellationToken ct);
    Task DeleteAsync(string assetId, CancellationToken ct);
}
