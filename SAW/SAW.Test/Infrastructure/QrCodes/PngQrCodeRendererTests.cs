using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SAW.Infrastructure.QrCodes;
using ZXing;

namespace SAW.Test.Infrastructure.QrCodes;

public sealed class PngQrCodeRendererTests
{
    [Fact]
    public void RealPng_DecodesToThePublicUrl_WithPrintableSizeAndQuietZone()
    {
        var url = "https://saw.example/trace/" + new string('a', 64);
        var png = new PngQrCodeRenderer().RenderPng(url);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Assert.Equal(width, height);
        Assert.True(width >= 600);
        Assert.Equal(1, png[24]); // QRCoder renders lossless 1-bit grayscale PNG.
        Assert.Equal(0, png[25]);
        using var compressed = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT") compressed.Write(png, offset + 8, length);
            offset += length + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        var scanlines = decoded.ToArray();
        var stride = (width + 7) / 8 + 1;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, scanlines[y * stride]); // PNG filter None from this renderer.
            for (var x = 0; x < width; x++)
                luminance[y * width + x] = (scanlines[y * stride + 1 + x / 8] & (128 >> (x % 8))) != 0 ? (byte)255 : (byte)0;
        }
        Assert.All(luminance.Take(width * 64), pixel => Assert.Equal(255, pixel)); // 4 modules × 16 pixels
        var source = new RGBLuminanceSource(luminance, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = new BarcodeReaderGeneric().Decode(source);
        Assert.NotNull(result);
        Assert.Equal(url, result.Text);
        Assert.Equal(BarcodeFormat.QR_CODE, result.BarcodeFormat);
    }
}
