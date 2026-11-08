using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAW.Application.Features.QrCodes.Interfaces;

namespace SAW.Infrastructure.QrCodes;

public sealed class CloudinaryQrImageStorage(HttpClient client, CloudinaryOptions options) : IQrImageStorage
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CloudName)
        && options.CloudName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && !string.IsNullOrWhiteSpace(options.ApiKey) && !string.IsNullOrWhiteSpace(options.ApiSecret);

    public async Task<string> UploadAsync(string assetId, byte[] png, CancellationToken ct)
    {
        var fields = SignedFields(new() { ["public_id"] = assetId, ["overwrite"] = "false" });
        using var content = new MultipartFormDataContent();
        foreach (var (key, value) in fields) content.Add(new StringContent(value), key);
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "qr.png");
        using var response = await client.PostAsync(Endpoint("upload"), content, ct);
        // Do not log Cloudinary response bodies (may include credentials/signature input).
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.GetProperty("public_id").GetString() != assetId)
            throw new InvalidOperationException("Unexpected QR asset ID from storage.");
        return json.RootElement.GetProperty("secure_url").GetString()
            ?? throw new InvalidOperationException("QR storage did not return an image URL.");
    }

    public async Task DeleteAsync(string assetId, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(SignedFields(new() { ["public_id"] = assetId, ["invalidate"] = "true" }));
        using var response = await client.PostAsync(Endpoint("destroy"), content, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.GetProperty("result").GetString() is not ("ok" or "not found"))
            throw new InvalidOperationException("QR asset cleanup failed.");
    }

    private string Endpoint(string action) => $"https://api.cloudinary.com/v1_1/{options.CloudName}/image/{action}";

    private Dictionary<string, string> SignedFields(Dictionary<string, string> fields)
    {
        if (!IsConfigured) throw new InvalidOperationException("Backend Cloudinary configuration is missing.");
        fields["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var input = string.Join("&", fields.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
        fields["signature"] = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input + options.ApiSecret))).ToLowerInvariant();
        fields["api_key"] = options.ApiKey;
        return fields;
    }
}
