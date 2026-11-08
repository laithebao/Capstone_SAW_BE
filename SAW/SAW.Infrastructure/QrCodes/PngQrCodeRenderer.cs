using QRCoder;
using SAW.Application.Features.QrCodes.Interfaces;

namespace SAW.Infrastructure.QrCodes;

public sealed class PngQrCodeRenderer : IQrCodeRenderer
{
    public byte[] RenderPng(string traceabilityUrl)
    {
        using var data = QRCodeGenerator.GenerateQrCode(traceabilityUrl, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(16, drawQuietZones: true);
    }
}
