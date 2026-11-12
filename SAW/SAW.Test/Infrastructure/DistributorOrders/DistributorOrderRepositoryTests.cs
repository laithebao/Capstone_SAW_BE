using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.DistributorOrders;

public sealed class DistributorOrderRepositoryTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static DistributorOrderRepository Repository(AppDbContext db) => new((DbContextOptions<AppDbContext>)db.GetService<IDbContextOptions>(), new HttpContextAccessor());

    private static async Task<ProductBatch> SeedAsync(AppDbContext db)
    {
        var customer = new Account { AccountId = 1, Username = "customer", Email = "customer@test.invalid", PasswordHash = "hash", FullName = "Customer", AccountStatus = "ACTIVE" };
        db.Distributors.Add(new Distributor { DistributorId = 1, Account = customer, DistributorCode = "D1", DistributorName = "Customer", TaxCode = "0123456789" });
        var batch = new ProductBatch { ProductBatchId = 10, BatchCode = "B-10", ProductName = "Mango", Unit = "kg", BatchStatus = "IN_STOCK", QualityGrade = "A", WeightInKg = 120,
            VerifiedWeightInKg = 100, ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(5),
            CropType = new CropType { CropCode = "MANGO", CropName = "Mango", CategoryName = "Fruit", IsActive = true },
            Supplier = new Supplier { SupplierCode = "S1", SupplierName = "Supplier", TaxCode = "1234567890", ContactPerson = "Contact", Address = "Private address" },
            GrowingArea = new GrowingArea { AreaName = "Area", Region = "South", Province = "Province", District = "District", Ward = "Ward" },
            Inventories = [new Inventory { QuantityOnHand = 60, WarehouseLocation = new WarehouseLocation { LocationCode = "L1", ZoneName = "Zone" } },
                new Inventory { QuantityOnHand = 40, WarehouseLocation = new WarehouseLocation { LocationCode = "L2", ZoneName = "Zone" } }] };
        db.Add(batch); await db.SaveChangesAsync();
        db.Add(new BatchSaleOffer { ProductBatch = batch, WholeLotPrice = 1000000, IsPublished = true });
        db.Add(new QcInspection { ProductBatch = batch, InspectionCode = "QC1", InspectionStatus = "COMPLETED", QcResult = "PASS", QualityGrade = "A",
            StartedAt = DateTime.UtcNow.AddDays(-2), CompletedAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync(); return batch;
    }

    [Fact]
    public async Task PublicCatalogUsesVerifiedWholeLotWeightAndDoesNotExposeSupplierContactData()
    {
        await using var db = Database(); await SeedAsync(db);
        var result = await Repository(db).PublicCatalogAsync(new DistributorQuery(Search: "Mango"), default);
        var lot = Assert.Single(result.Items); Assert.Equal(100, lot.WeightKg); Assert.Equal(1000000, lot.WholeLotPrice); Assert.Equal(1, result.TotalCount);
        var json = System.Text.Json.JsonSerializer.Serialize(lot);
        Assert.DoesNotContain("Private address", json); Assert.DoesNotContain("1234567890", json);
        Assert.Empty((await Repository(db).PublicCatalogAsync(new DistributorQuery(Search: "does-not-exist"), default)).Items);
    }

    [Theory]
    [InlineData("unpublished")]
    [InlineData("zero-price")]
    [InlineData("inactive-crop")]
    [InlineData("expired")]
    [InlineData("unverified")]
    [InlineData("reserved")]
    [InlineData("wrong-unit")]
    [InlineData("inactive-location")]
    [InlineData("stock-mismatch")]
    [InlineData("pending-qc")]
    [InlineData("qc-failed")]
    [InlineData("qc-grade-mismatch")]
    [InlineData("newer-draft-qc")]
    public async Task CatalogExcludesLotWhenAnyCurrentSaleEligibilityConditionFails(string condition)
    {
        await using var db = Database(); var batch = await SeedAsync(db); var offer = await db.BatchSaleOffers.SingleAsync();
        var stock = batch.Inventories.First(); var qc = await db.QcInspections.SingleAsync();
        switch (condition)
        {
            case "unpublished": offer.IsPublished = false; break;
            case "zero-price": offer.WholeLotPrice = 0; break;
            case "inactive-crop": batch.CropType.IsActive = false; break;
            case "expired": batch.ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(-1); break;
            case "unverified": batch.VerifiedWeightInKg = null; break;
            case "reserved": stock.ReservedQuantity = 1; break;
            case "wrong-unit": stock.Unit = "box"; break;
            case "inactive-location": stock.WarehouseLocation.LocationStatus = "INACTIVE"; break;
            case "stock-mismatch": stock.QuantityOnHand--; break;
            case "pending-qc": batch.BatchStatus = "PENDING_QC"; break;
            case "qc-failed": qc.QcResult = "FAIL"; break;
            case "qc-grade-mismatch": qc.QualityGrade = "B"; break;
            case "newer-draft-qc": db.Add(new QcInspection { ProductBatch = batch, InspectionCode = "QC2", InspectionStatus = "DRAFT", StartedAt = DateTime.UtcNow }); break;
        }
        await db.SaveChangesAsync(); Assert.Empty((await Repository(db).PublicCatalogAsync(new DistributorQuery(), default)).Items);
        Assert.Empty((await Repository(db).CatalogAsync(1, new DistributorQuery(), default)).Items);
    }

    [Theory]
    [InlineData("PENDING", false)]
    [InlineData("DISPATCHED", false)]
    [InlineData("DELIVERY_REFUSED", false)]
    [InlineData("CANCELLED", true)]
    [InlineData("REJECTED", true)]
    public async Task ExistingOrderKeepsLotOffCatalogUntilCancelledOrRejected(string status, bool visible)
    {
        await using var db = Database(); var batch = await SeedAsync(db);
        db.Add(new PurchaseOrder { DistributorId = 1, OrderCode = "O1", DeliveryAddress = "Address", OrderStatus = status,
            OrderDetails = [new OrderDetail { RequestedProductBatch = batch, Unit = "kg" }] }); await db.SaveChangesAsync();
        Assert.Equal(visible ? 1 : 0, (await Repository(db).PublicCatalogAsync(new DistributorQuery(), default)).TotalCount);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("unknown")]
    public async Task PrivateCatalogAndDashboardRequireAnActiveDistributor(string condition)
    {
        await using var db = Database(); await SeedAsync(db);
        if (condition == "account") (await db.Accounts.FindAsync(1))!.AccountStatus = "INACTIVE";
        if (condition == "profile") (await db.Distributors.FindAsync(1))!.ProfileStatus = "INACTIVE";
        await db.SaveChangesAsync(); var actor = condition == "unknown" ? 99 : 1;
        await Assert.ThrowsAsync<ForbiddenException>(() => Repository(db).CatalogAsync(actor, new DistributorQuery(), default));
        await Assert.ThrowsAsync<ForbiddenException>(() => Repository(db).DashboardAsync(actor, default));
        Assert.Single((await Repository(db).PublicCatalogAsync(new DistributorQuery(), default)).Items);
    }

    [Fact]
    public async Task DashboardCountsAllOwnedOrdersButSpendingOnlyConfirmedReceiptsAndAcceptedPartialLots()
    {
        await using var db = Database(); await SeedAsync(db); var at = DateTime.UtcNow;
        var orders = new[] { "PENDING", "CANCELLED", "DELIVERED", "PARTIALLY_REFUSED", "PARTIALLY_REFUSED", "DELIVERY_REFUSED" }
            .Select((status, index) => new PurchaseOrder { PurchaseOrderId = index + 1, DistributorId = 1, OrderCode = $"O{index}", DeliveryAddress = "Address", OrderStatus = status,
                CreatedAt = at.AddMinutes(index), TotalAmount = 1000, ReceivedAt = index == 3 ? at : null }).ToArray();
        orders[3].OrderDetails.Add(new OrderDetail { OrderDetailId = 10, Unit = "kg", UnitPrice = 400, TaxAmount = 40 });
        orders[3].OrderDetails.Add(new OrderDetail { OrderDetailId = 11, Unit = "kg", UnitPrice = 600, TaxAmount = 60 });
        orders[4].OrderDetails.Add(new OrderDetail { Unit = "kg", UnitPrice = 900 });
        db.AddRange(orders); await db.SaveChangesAsync();
        db.OrderDeliveryRefusalLots.Add(new OrderDeliveryRefusalLot { OrderDetailId = 11 }); await db.SaveChangesAsync();
        var result = await Repository(db).DashboardAsync(1, default);
        Assert.Equal(1, result.PendingOrders); Assert.Equal(1, result.CancelledOrders); Assert.Equal(2, result.SuccessfulOrders); Assert.Equal(1440, result.TotalSpent);
        Assert.Equal(new long[] { 6, 5, 4, 3 }, result.RecentOrders.Select(o => o.Id));
    }
}
