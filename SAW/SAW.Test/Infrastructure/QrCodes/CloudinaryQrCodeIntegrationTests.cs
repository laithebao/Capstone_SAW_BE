using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Features.QrCodes.Services;
using SAW.Application.Repositories;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.QrCodes;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;
using ZXing;

namespace SAW.Test.Infrastructure.QrCodes;

public sealed class RealCloudinaryFactAttribute:FactAttribute
{
    public RealCloudinaryFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("SAW_TEST_REAL_CLOUDINARY")!="1"||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION")))
            Skip="Opt in to isolated SQL + real Cloudinary with SAW_TEST_REAL_CLOUDINARY=1 and local credentials.";
    }
}
public sealed class CloudinaryQrCodeIntegrationTests(ReceivingSqlFixture fixture):IClassFixture<ReceivingSqlFixture>
{
    [RealCloudinaryFact]
    public async Task Worker_uploads_real_cloudinary_png_download_decodes_configured_url_and_saves_one_active_qr()
    {
        var options=new CloudinaryOptions{CloudName=Environment.GetEnvironmentVariable("SAW_TEST_CLOUD_NAME")??"",
            ApiKey=Environment.GetEnvironmentVariable("SAW_TEST_CLOUD_KEY")??"",ApiSecret=Environment.GetEnvironmentVariable("SAW_TEST_CLOUD_SECRET")??""};
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(40)};
        var storage=new OwnedStorage(new CloudinaryQrImageStorage(http,options));
        Assert.True(storage.IsConfigured,"Local Cloudinary credentials are unavailable.");
        var batch=await fixture.BatchAsync();
        await using(var db=fixture.Context())
        {
            await db.ProductBatches.Where(b=>b.ProductBatchId==batch.ProductBatchId).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.BatchStatus,"APPROVED_FOR_STORAGE").SetProperty(b=>b.QualityGrade,"E"));
            var qc=fixture.Inspection(batch.ProductBatchId,"COMPLETED");qc.StartedAt=DateTime.UtcNow.AddMinutes(-2);qc.CompletedAt=DateTime.UtcNow.AddMinutes(-1);qc.QcResult="PASS";qc.QualityGrade="E";
            db.Add(qc);await db.SaveChangesAsync();
        }
        var qrOptions=new QrCodeOptions{PublicFrontendBaseUrl=Environment.GetEnvironmentVariable("SAW_TEST_PUBLIC_BASE")??"https://saw.test.invalid",IntervalSeconds=5,BatchSize=10};
        using var provider=new ServiceCollection().AddScoped<AppDbContext>(_=>fixture.Context()).AddScoped<IQrCodeRepository,QrCodeRepository>()
            .AddScoped<IQrImageStorage>(_=>storage).AddScoped<IProductBatchQrCodeService>(s=>new ProductBatchQrCodeService(
                s.GetRequiredService<IQrCodeRepository>(),new PngQrCodeRenderer(),storage,qrOptions,NullLogger<ProductBatchQrCodeService>.Instance)).BuildServiceProvider();
        try
        {
            using var worker=new QrCodeGenerationWorker(provider.GetRequiredService<IServiceScopeFactory>(),qrOptions,NullLogger<QrCodeGenerationWorker>.Instance);
            await worker.StartAsync(default);
            try
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(70));
                while(true){await using var check=fixture.Context();if(await check.QrCodes.AnyAsync(q=>q.ProductBatchId==batch.ProductBatchId,deadline.Token))break;await Task.Delay(200,deadline.Token);}
            }finally{await worker.StopAsync(default);}
            await using var db=fixture.Context();var qr=Assert.Single(await db.QrCodes.Where(q=>q.ProductBatchId==batch.ProductBatchId).ToListAsync());
            Assert.True(qr.IsActive);Assert.Equal(64,qr.PublicToken.Length);Assert.Equal(qrOptions.PublicFrontendBaseUrl.TrimEnd('/')+"/trace/"+qr.PublicToken,qr.TraceabilityUrl);
            using var response=await http.GetAsync(qr.QrImageUrl);response.EnsureSuccessStatusCode();
            Assert.Equal("image/png",response.Content.Headers.ContentType!.MediaType);
            var png=await response.Content.ReadAsByteArrayAsync();var decoded=Decode(png);Assert.Equal(qr.TraceabilityUrl,decoded);
            var evidence=Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY");
            if(!string.IsNullOrEmpty(evidence))await File.WriteAllTextAsync(Path.Combine(evidence,"Cloudinary-real-proof.json"),JsonSerializer.Serialize(new{
                BatchCode=batch.BatchCode,qr.PublicToken,qr.TraceabilityUrl,qr.QrImageUrl,DownloadedBytes=png.Length,DecodedUrl=decoded,ActiveQrCount=1,
                Upload="Real Cloudinary",Cleanup="Owned test asset is deleted in finally; isolated DB dropped by fixture."},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally {await storage.CleanupAsync();}
    }
    private sealed class OwnedStorage(CloudinaryQrImageStorage real):IQrImageStorage
    {
        private readonly HashSet<string> owned=[];
        public bool IsConfigured=>real.IsConfigured;
        public Task<string> UploadAsync(string id,byte[] png,CancellationToken ct){Assert.Matches("^saw-qr/[a-f0-9]{32}$",id);owned.Add(id);return real.UploadAsync(id,png,ct);}
        public Task DeleteAsync(string id,CancellationToken ct){Assert.Contains(id,owned);return real.DeleteAsync(id,ct);}
        public async Task CleanupAsync(){foreach(var id in owned)await real.DeleteAsync(id,default);}
    }
    private static string Decode(byte[] png)
    {
        Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},png[..8]);
        var width=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4));var height=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4));
        Assert.Equal(1,png[24]);Assert.Equal(0,png[25]);
        using var compressed=new MemoryStream();for(var offset=8;offset<png.Length;){var length=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset,4));
            if(Encoding.ASCII.GetString(png,offset+4,4)=="IDAT")compressed.Write(png,offset+8,length);offset+=length+12;}
        compressed.Position=0;using var zlib=new ZLibStream(compressed,CompressionMode.Decompress);using var decoded=new MemoryStream();zlib.CopyTo(decoded);
        var bytes=decoded.ToArray();var stride=(width+7)/8+1;var luminance=new byte[width*height];
        for(var y=0;y<height;y++){Assert.Equal(0,bytes[y*stride]);for(var x=0;x<width;x++)luminance[y*width+x]=(bytes[y*stride+1+x/8]&(128>>(x%8)))!=0?(byte)255:(byte)0;}
        var result=new BarcodeReaderGeneric().Decode(new RGBLuminanceSource(luminance,width,height,RGBLuminanceSource.BitmapFormat.Gray8));
        Assert.NotNull(result);return result.Text;
    }
}
