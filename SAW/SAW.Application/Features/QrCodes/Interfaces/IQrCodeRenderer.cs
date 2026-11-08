namespace SAW.Application.Features.QrCodes.Interfaces;

public interface IQrCodeRenderer
{
    byte[] RenderPng(string traceabilityUrl);
}
