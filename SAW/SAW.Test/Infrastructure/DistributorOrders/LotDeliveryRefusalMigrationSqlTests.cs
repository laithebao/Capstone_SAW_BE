using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Domain.Entities;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.DistributorOrders;

public sealed class LotDeliveryRefusalMigrationSqlTests
{
    [ReceivingSqlFact]
    public async Task ExistingWholeOrderRefusal_KeepsAllOriginalLotsReasonAndPhotos_WhenLotScopeIsAdded()
    {
        var fixture = new ReceivingSqlFixture(useMigrationBaseline: true);
        try
        {
            await fixture.InitializeAsync();
            await using var db = fixture.Context();
            var migrations = db.GetService<IMigrator>();
            await migrations.MigrateAsync("20261031135256_AddOrderDeliveryRefusal");
            var batch = await fixture.BatchAsync();
            var second = await fixture.BatchAsync();
            var order = new PurchaseOrder { OrderCode = "LEGACY-FULL-REFUSAL", DeliveryAddress = "Test", OrderStatus = "DELIVERY_REFUSED",
                Distributor = new Distributor { AccountId = fixture.ActorId, DistributorCode = "LEGACY-FULL", DistributorName = "Test", TaxCode = "TEST" } };
            foreach (var batchId in new[] { batch.ProductBatchId, second.ProductBatchId })
                order.OrderDetails.Add(new OrderDetail { CropTypeId = fixture.CropTypeId, RequestedProductBatchId = batchId,
                    RequestedQuantity = 1, ApprovedQuantity = 1, RequestedWeightKg = 100, ApprovedWeightKg = 100, Unit = "lot", UnitPrice = 1500000 });
            db.PurchaseOrders.Add(order); await db.SaveChangesAsync();
            var imageId = Guid.NewGuid();
            var report = new OrderDeliveryRefusal { PurchaseOrderId = order.PurchaseOrderId, PurchaseOrder = order, Reason = "Original reason",
                RequestId = Guid.NewGuid(), CreatedByAccountId = fixture.ActorId, CreatedAt = DateTime.UtcNow };
            report.Images.Add(new OrderDeliveryRefusalImage { OrderDeliveryRefusalImageId = imageId, FileName = "original.png", StorageName = "original.png",
                ContentType = "image/png", Sha256 = new string('A',64), ByteLength = 100, Position = 0 });
            db.OrderDeliveryRefusals.Add(report); await db.SaveChangesAsync();
            await migrations.MigrateAsync();
            Assert.Equal(2, await db.OrderDeliveryRefusalLots.CountAsync(l => l.OrderDeliveryRefusalId == report.OrderDeliveryRefusalId));
            Assert.Equal("Original reason", (await db.OrderDeliveryRefusals.AsNoTracking().SingleAsync()).Reason);
            Assert.Equal(imageId, (await db.OrderDeliveryRefusalImages.AsNoTracking().SingleAsync()).OrderDeliveryRefusalImageId);
            Assert.Equal("DELIVERY_REFUSED", (await db.PurchaseOrders.AsNoTracking().SingleAsync()).OrderStatus);
            await Assert.ThrowsAnyAsync<Exception>(() => migrations.MigrateAsync("20261031135256_AddOrderDeliveryRefusal"));
            Assert.Equal(2, await db.OrderDeliveryRefusalLots.CountAsync());
            Assert.Equal(1, await db.OrderDeliveryRefusalImages.CountAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }
}
