using System.Net;
using System.Security.Cryptography;
using System.Text;
using SAW.Infrastructure.QrCodes;

namespace SAW.Test.Infrastructure.QrCodes;

public sealed class CloudinaryQrImageStorageTests
{
    [Fact]
    public async Task UploadAndCleanup_UseSignedServerRequests_AndTheExactOwnedAssetId()
    {
        var calls = new List<string>();
        var handler = new Handler(async request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("api.cloudinary.com", request.RequestUri.Host);
            calls.Add(request.RequestUri.AbsolutePath);
            if (request.Content is MultipartFormDataContent multipart)
            {
                var fields = new Dictionary<string, string>();
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "file")
                    {
                        Assert.Equal("image/png", part.Headers.ContentType!.MediaType);
                        Assert.Equal(new byte[] { 137, 80, 78, 71 }, await part.ReadAsByteArrayAsync());
                    }
                    else fields[name] = await part.ReadAsStringAsync();
                }
                Assert.Equal("saw-qr/owned", fields["public_id"]);
                Assert.Equal("false", fields["overwrite"]);
                var input = $"overwrite=false&public_id=saw-qr/owned&timestamp={fields["timestamp"]}test-secret";
                Assert.Equal(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant(), fields["signature"]);
                Assert.DoesNotContain("test-secret", string.Join("", fields.Values));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"public_id":"saw-qr/owned","secure_url":"https://res.cloudinary.com/test/image/upload/owned.png"}
                    """) };
            }
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("public_id=saw-qr%2Fowned", body);
            Assert.Contains("invalidate=true", body);
            Assert.DoesNotContain("test-secret", body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":\"ok\"}") };
        });
        using var client = new HttpClient(handler);
        var storage = new CloudinaryQrImageStorage(client, new CloudinaryOptions { CloudName = "test", ApiKey = "test-key", ApiSecret = "test-secret" });
        Assert.Equal("https://res.cloudinary.com/test/image/upload/owned.png", await storage.UploadAsync("saw-qr/owned", [137, 80, 78, 71], default));
        await storage.DeleteAsync("saw-qr/owned", default);
        Assert.Equal(new[] { "/v1_1/test/image/upload", "/v1_1/test/image/destroy" }, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
