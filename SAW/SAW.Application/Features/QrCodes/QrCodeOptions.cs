namespace SAW.Application.Features.QrCodes;

public sealed class QrCodeOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 10;
    public string PublicFrontendBaseUrl { get; set; } = "";

    public bool HasValidPublicUrl => Uri.TryCreate(PublicFrontendBaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && PublicFrontendBaseUrl.Length <= 850;
}
