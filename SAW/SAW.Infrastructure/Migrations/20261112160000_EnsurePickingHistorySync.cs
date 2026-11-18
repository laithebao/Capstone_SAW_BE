using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261112160000_EnsurePickingHistorySync")]
public sealed class EnsurePickingHistorySync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var definition = Definition.Replace("'", "''");
        migrationBuilder.Sql($"""
            IF OBJECT_ID(N'dbo.PICKING_HISTORY',N'U') IS NULL
                THROW 50312,'Install the warehouse database baseline before the picking trigger migration.',1;
            IF OBJECT_ID(N'dbo.TR_PICKING_HISTORY_SYNC',N'TR') IS NOT NULL
            BEGIN
                DECLARE @actual nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'dbo.TR_PICKING_HISTORY_SYNC',N'TR'));
                DECLARE @expected nvarchar(max)=N'{definition}';
                IF @actual IS NULL
                    THROW 50310,'Picking trigger definition is unreadable. Review it before migration.',1;
                SELECT @actual=UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@actual,N' ',N''),CHAR(9),N''),CHAR(10),N''),CHAR(13),N''),N'[',N''),N']',N'')),
                    @expected=UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@expected,N' ',N''),CHAR(9),N''),CHAR(10),N''),CHAR(13),N''),N'[',N''),N']',N''));
                SET @actual=REPLACE(REPLACE(@actual,N'CREATEORALTERTRIGGER',N'CREATETRIGGER'),N'ALTERTRIGGER',N'CREATETRIGGER');
                IF @actual<>@expected
                    THROW 50310,'Picking trigger differs from the reviewed definition. Review local changes before migration.',1;
                IF EXISTS(SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N'dbo.TR_PICKING_HISTORY_SYNC',N'TR') AND is_disabled=1)
                    THROW 50311,'Picking trigger is disabled. Review the operational configuration before migration.',1;
            END;
            """);
        // Adopt an existing reviewed trigger without recreating it. A missing
        // trigger is installed in the EF migration transaction; no stock is replayed.
        migrationBuilder.Sql($"""
            IF OBJECT_ID(N'dbo.TR_PICKING_HISTORY_SYNC',N'TR') IS NULL
                EXEC(N'{definition}');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Earlier application versions also depend on this baseline trigger.
        // Preserve it on rollback, including triggers adopted rather than created.
    }

    internal const string Definition = """
        CREATE TRIGGER dbo.TR_PICKING_HISTORY_SYNC
        ON dbo.PICKING_HISTORY
        AFTER INSERT
        AS
        BEGIN
            SET NOCOUNT ON;
            ;WITH Affected AS (
                SELECT DISTINCT InventoryReservationID FROM inserted
            ),
            Totals AS (
                SELECT r.InventoryReservationID,r.ReservedQuantity,
                    ISNULL(SUM(ph.PickedQuantity),0) AS TotalPicked
                FROM dbo.INVENTORY_RESERVATION r
                JOIN Affected a ON a.InventoryReservationID=r.InventoryReservationID
                LEFT JOIN dbo.PICKING_HISTORY ph ON ph.InventoryReservationID=r.InventoryReservationID
                GROUP BY r.InventoryReservationID,r.ReservedQuantity
            )
            UPDATE r SET PickedQuantity=t.TotalPicked,
                ReservationStatus=CASE WHEN t.TotalPicked=0 THEN N'RESERVED'
                    WHEN t.TotalPicked<t.ReservedQuantity THEN N'PARTIALLY_PICKED' ELSE N'PICKED' END
            FROM dbo.INVENTORY_RESERVATION r
            JOIN Totals t ON t.InventoryReservationID=r.InventoryReservationID;
            IF EXISTS (
                SELECT 1 FROM dbo.INVENTORY_RESERVATION r
                JOIN inserted i ON i.InventoryReservationID=r.InventoryReservationID
                WHERE r.PickedQuantity>r.ReservedQuantity
            )
            BEGIN
                THROW 50105, 'Picked quantity cannot exceed reserved quantity.', 1;
            END
        END;
        """;
}
