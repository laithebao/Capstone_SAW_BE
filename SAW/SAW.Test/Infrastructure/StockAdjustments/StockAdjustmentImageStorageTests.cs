using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.QrCodes;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.QrCodes;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed class StockAdjustmentImageStorageTests
{
    [RealCloudinaryFact]
    public async Task Real_image_upload_download_and_pending_request_link_are_persisted_then_owned_fixture_is_cleaned()
    {
        var f=new StockAdjustmentSqlFixture();await f.InitializeAsync();
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(40)};
        var real=new CloudinaryQrImageStorage(http,new CloudinaryOptions{CloudName=Environment.GetEnvironmentVariable("SAW_TEST_CLOUD_NAME")??"",
            ApiKey=Environment.GetEnvironmentVariable("SAW_TEST_CLOUD_KEY")??"",ApiSecret=Environment.GetEnvironmentVariable("SAW_TEST_CLOUD_SECRET")??""});
        var owned=new OwnedStorage(real);
        try
        {
            Assert.True(real.IsConfigured,"Local Cloudinary configuration is unavailable.");
            var dbFixture=f.Database;var batch=await dbFixture.BatchAsync();
            await using(var db=dbFixture.Context())
            {
                db.Inventories.Add(new Inventory{ProductBatchId=batch.ProductBatchId,Unit="kg",QuantityOnHand=100,LastUpdatedAt=DateTime.UtcNow,
                    WarehouseLocation=new WarehouseLocation{LocationCode="REAL-IMAGE",ZoneName="Fixture",LocationStatus="ACTIVE"}});await db.SaveChangesAsync();
            }
            var service=new StockAdjustmentService(new StockAdjustmentRepository(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(dbFixture.ConnectionString,s=>s.EnableRetryOnFailure()).Options,new HttpContextAccessor(),owned));
            var image=await service.UploadAsync(new PngQrCodeRenderer().RenderPng("https://test.invalid/adjustment-evidence"),dbFixture.ActorId,default);
            var bytes=await http.GetByteArrayAsync(image.Url);Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},bytes[..8]);
            Assert.NotEmpty(StockAdjustmentRepository.ValidateImage(bytes));
            var scope=await service.ScopeAsync(batch.ProductBatchId,"DISPOSAL",null,default);
            var request=new CreateStockAdjustmentRequest(Guid.NewGuid(),batch.ProductBatchId,"DISPOSAL",null,null,"DAMAGE","Real upload fixture",scope.Snapshot,[image.Id]);
            var saved=await service.CreateAsync(request,dbFixture.ActorId,default);Assert.Equal(image,Assert.Single(saved.Images));Assert.Equal("PENDING",saved.Summary.Status);
            await using(var db=dbFixture.Context()){Assert.Equal(100,await db.Inventories.Where(i=>i.ProductBatchId==batch.ProductBatchId).Select(i=>i.QuantityOnHand).SingleAsync());Assert.Equal(saved.Summary.Id,(await db.StockAdjustmentImages.FindAsync(image.Id))!.StockAdjustmentId);}
        }
        finally
        {
            // Drop only this GUID fixture first: no referenced production or test image is deleted.
            await f.DisposeAsync();await owned.CleanupAsync();
        }
    }
    private sealed class OwnedStorage(CloudinaryQrImageStorage storage):IQrImageStorage
    {
        private readonly HashSet<string> assets=[];
        public bool IsConfigured=>storage.IsConfigured;
        public Task<string> UploadAsync(string id,byte[] png,CancellationToken ct){Assert.Matches("^saw-stock-adjustments/[a-f0-9]{32}$",id);assets.Add(id);return storage.UploadAsync(id,png,ct);}
        public Task DeleteAsync(string id,CancellationToken ct){Assert.Contains(id,assets);return storage.DeleteAsync(id,ct);}
        public async Task CleanupAsync(){foreach(var id in assets)await storage.DeleteAsync(id,default);}
    }
}
