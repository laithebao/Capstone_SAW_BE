namespace SAW.Application.Features.QrCodes.Dtos;

public sealed record ProductBatchQrCodeResponse(
    long ProductBatchId, string BatchCode, string Status, string Message,
    string? PublicToken = null, string? TraceabilityUrl = null,
    string? QrImageUrl = null, DateTime? GeneratedAt = null);
