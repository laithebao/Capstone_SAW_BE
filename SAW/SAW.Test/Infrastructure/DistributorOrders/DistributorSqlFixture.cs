using Microsoft.EntityFrameworkCore;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.DistributorOrders;

public sealed class DistributorSqlFixture : IAsyncLifetime
{
    public ReceivingSqlFixture Database { get; } = new();
    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION"))) return;
        try
        {
            await using var db = Database.Context();
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE ORDER_DETAIL DROP COLUMN LineSubtotal, LineTotal;
                ALTER TABLE ORDER_DETAIL ADD LineSubtotal AS CONVERT(decimal(18,2), RequestedQuantity * UnitPrice) PERSISTED;
                ALTER TABLE ORDER_DETAIL ADD LineTotal AS CONVERT(decimal(18,2), RequestedQuantity * UnitPrice + TaxAmount) PERSISTED;
                ALTER TABLE INVENTORY DROP COLUMN AvailableQuantity;
                ALTER TABLE INVENTORY ADD AvailableQuantity AS (QuantityOnHand - ReservedQuantity) PERSISTED;
                ALTER TABLE INVENTORY ADD CONSTRAINT CK_INVENTORY_Quantity CHECK (QuantityOnHand>=0 AND ReservedQuantity>=0 AND ReservedQuantity<=QuantityOnHand);
                """);
            // Exact trigger body from the supplied warehouse export.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE   TRIGGER [dbo].[TR_PURCHASE_ORDER_STATUS_HISTORY]
                ON [dbo].[PURCHASE_ORDER]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                
                    INSERT INTO dbo.ORDER_STATUS_HISTORY
                        (PurchaseOrderID, OldStatus, NewStatus, ChangedByAccountID, ChangedAt)
                    SELECT
                        i.PurchaseOrderID,
                        d.OrderStatus,
                        i.OrderStatus,
                        TRY_CONVERT(INT, SESSION_CONTEXT(N'AccountID')),
                        SYSDATETIME()
                    FROM inserted i
                    LEFT JOIN deleted d
                        ON d.PurchaseOrderID = i.PurchaseOrderID
                    WHERE d.PurchaseOrderID IS NULL
                       OR ISNULL(d.OrderStatus, N'') <> ISNULL(i.OrderStatus, N'');
                END;
                """);
            // Exact trigger body from the supplied warehouse export.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE   TRIGGER [dbo].[TR_ORDER_STATUS_HISTORY_IMMUTABLE]
                ON [dbo].[ORDER_STATUS_HISTORY]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    THROW 50104, 'ORDER_STATUS_HISTORY records are immutable.', 1;
                END;
                """);
        }
        catch { await Database.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Database.DisposeAsync();
}
