using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizePurchaseOrderHistoryUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The legacy trigger used SYSDATETIME(), so existing history rows represent
            // server-local time. Normalize those rows only when that trigger was present.
            // A database created solely from EF migrations may have UTC fallback rows and no trigger.
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.TR_PURCHASE_ORDER_STATUS_HISTORY', N'TR') IS NOT NULL
                BEGIN
                    DECLARE @ServerOffsetMinutes int = DATEPART(TZOFFSET, SYSDATETIMEOFFSET());
                    DECLARE @HistoryIsImmutable bit =
                        CASE WHEN OBJECT_ID(N'dbo.TR_ORDER_STATUS_HISTORY_IMMUTABLE', N'TR') IS NULL THEN 0 ELSE 1 END;

                    IF @HistoryIsImmutable = 1
                        DISABLE TRIGGER dbo.TR_ORDER_STATUS_HISTORY_IMMUTABLE ON dbo.ORDER_STATUS_HISTORY;

                    BEGIN TRY
                        UPDATE dbo.ORDER_STATUS_HISTORY
                        SET ChangedAt = DATEADD(MINUTE, -@ServerOffsetMinutes, ChangedAt);
                    END TRY
                    BEGIN CATCH
                        IF @HistoryIsImmutable = 1
                            ENABLE TRIGGER dbo.TR_ORDER_STATUS_HISTORY_IMMUTABLE ON dbo.ORDER_STATUS_HISTORY;
                        THROW;
                    END CATCH;

                    IF @HistoryIsImmutable = 1
                        ENABLE TRIGGER dbo.TR_ORDER_STATUS_HISTORY_IMMUTABLE ON dbo.ORDER_STATUS_HISTORY;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER dbo.TR_PURCHASE_ORDER_STATUS_HISTORY
                ON dbo.PURCHASE_ORDER
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
                        SYSUTCDATETIME()
                    FROM inserted i
                    LEFT JOIN deleted d ON d.PurchaseOrderID = i.PurchaseOrderID
                    WHERE d.PurchaseOrderID IS NULL
                       OR ISNULL(d.OrderStatus, N'') <> ISNULL(i.OrderStatus, N'');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER dbo.TR_PURCHASE_ORDER_STATUS_HISTORY
                ON dbo.PURCHASE_ORDER
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.ORDER_STATUS_HISTORY
                        (PurchaseOrderID, OldStatus, NewStatus, ChangedByAccountID, ChangedAt)
                    SELECT i.PurchaseOrderID, d.OrderStatus, i.OrderStatus,
                        TRY_CONVERT(INT, SESSION_CONTEXT(N'AccountID')), SYSDATETIME()
                    FROM inserted i
                    LEFT JOIN deleted d ON d.PurchaseOrderID = i.PurchaseOrderID
                    WHERE d.PurchaseOrderID IS NULL
                       OR ISNULL(d.OrderStatus, N'') <> ISNULL(i.OrderStatus, N'');
                END;
                """);

            migrationBuilder.Sql("""
                DECLARE @ServerOffsetMinutes int = DATEPART(TZOFFSET, SYSDATETIMEOFFSET());
                UPDATE dbo.ORDER_STATUS_HISTORY
                SET ChangedAt = DATEADD(MINUTE, @ServerOffsetMinutes, ChangedAt);
                """);
        }
    }
}
