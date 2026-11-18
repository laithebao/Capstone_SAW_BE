using Microsoft.EntityFrameworkCore;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.Picking;

public sealed class PickingSqlFixture : IAsyncLifetime
{
    public ReceivingSqlFixture Database { get; } = new();
    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        var source = Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION");
        if (string.IsNullOrEmpty(source)) return;
        try
        {
            await using var db = Database.Context();
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE INVENTORY DROP COLUMN AvailableQuantity;
                ALTER TABLE INVENTORY ADD AvailableQuantity AS (QuantityOnHand - ReservedQuantity) PERSISTED;
                ALTER TABLE ORDER_DETAIL DROP COLUMN LineSubtotal, LineTotal;
                ALTER TABLE ORDER_DETAIL ADD LineSubtotal AS CONVERT(decimal(18,2),RequestedQuantity*UnitPrice) PERSISTED;
                ALTER TABLE ORDER_DETAIL ADD LineTotal AS CONVERT(decimal(18,2),RequestedQuantity*UnitPrice+TaxAmount) PERSISTED;
                ALTER TABLE INVENTORY ALTER COLUMN LastUpdatedAt datetime2(0) NOT NULL;
                ALTER TABLE PICKING_HISTORY ALTER COLUMN PickedAt datetime2(0) NOT NULL;
                ALTER TABLE INVENTORY_RESERVATION ALTER COLUMN ReservedAt datetime2(0) NOT NULL;
                CREATE INDEX IX_PICKING_HISTORY_Reservation_Date ON PICKING_HISTORY(InventoryReservationID,PickedAt);
                CREATE INDEX IX_INVENTORY_RESERVATION_OrderDetail_Status ON INVENTORY_RESERVATION(OrderDetailID,ReservationStatus);
                """);
            // Versioned real definitions make the fixture independent of the supplied database's
            // tables/triggers. SAW_TEST_SQL_CONNECTION only supplies server/login (master is valid).
            await WarehouseWorkflowTestSchema.InstallAsync(db, "SAW.Test.Infrastructure.Picking.PickingSchema.sql");
        }
        catch (Exception error) { await Database.CleanupAfterInitializationFailureAsync(error); throw; }
    }
    public Task DisposeAsync() => Database.DisposeAsync();
}
