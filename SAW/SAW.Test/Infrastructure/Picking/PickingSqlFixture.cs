using Microsoft.Data.SqlClient;
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
            // Read actual trigger/check definitions. Only install into this fixture's GUID database.
            await using var live = new SqlConnection(source); await live.OpenAsync();
            await using var command = live.CreateCommand();
            command.CommandText = """
                SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name IN
                ('TR_PICKING_HISTORY_SYNC','TR_PICKING_HISTORY_IMMUTABLE','TR_INVENTORY_RESERVATION_SYNC',
                 'TR_INVENTORY_RESERVATION_INTEGRITY','TR_PURCHASE_ORDER_STATUS_HISTORY','TR_ORDER_STATUS_HISTORY_IMMUTABLE',
                 'TR_PRODUCT_BATCH_STATUS_HISTORY','TR_BATCH_STATUS_HISTORY_IMMUTABLE','TR_AUDIT_LOG_IMMUTABLE');
                SELECT OBJECT_NAME(parent_object_id),name,definition FROM sys.check_constraints
                WHERE OBJECT_NAME(parent_object_id) IN ('INVENTORY','INVENTORY_RESERVATION','PICKING_HISTORY','ORDER_DETAIL','PURCHASE_ORDER','AUDIT_LOG');
                """;
            var triggers = new List<string>(); var checks = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) triggers.Add(reader.GetString(0));
                await reader.NextResultAsync();
                while (await reader.ReadAsync()) checks.Add($"ALTER TABLE [{reader.GetString(0)}] ADD CONSTRAINT [{reader.GetString(1)}] CHECK {reader.GetString(2)}");
            }
            Assert.Equal(9,triggers.Count);
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
            foreach (var check in checks) await db.Database.ExecuteSqlRawAsync(check);
            foreach (var trigger in triggers) await db.Database.ExecuteSqlRawAsync(trigger);
        }
        catch { await Database.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Database.DisposeAsync();
}
