using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations;

/// <summary>Definition-only change. Legacy definitions verified from dev sys.sql_modules before deployment.</summary>
public partial class FixGoodsIssueWorkflowTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(VerifyKnownDefinitions);
        migrationBuilder.Sql(ReservationSyncV1);
        migrationBuilder.Sql(IssueDetailIntegrityV1);
    }

    // Deploy the previous application version together with Down. This restores definitions only;
    // it cannot undo stock movements or convert data created by the newer workflow.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(VerifyKnownDefinitions);
        migrationBuilder.Sql(ReservationSyncLegacy);
        migrationBuilder.Sql(IssueDetailIntegrityLegacy);
    }

    private const string VerifyKnownDefinitions = """
        -- Accept only verified legacy definitions, the reviewed V1 script, or absent triggers.
        -- Unknown local/member changes must be reviewed rather than silently overwritten.
        DECLARE @Trigger sysname, @Definition nvarchar(max), @Canonical nvarchar(max), @Hash varbinary(32);
        DECLARE known CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM (VALUES
            (N'TR_INVENTORY_RESERVATION_SYNC'),(N'TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK')) t(name);
        OPEN known;
        FETCH NEXT FROM known INTO @Trigger;
        WHILE @@FETCH_STATUS=0
        BEGIN
            SET @Definition=OBJECT_DEFINITION(OBJECT_ID(N'dbo.'+@Trigger,N'TR'));
            IF OBJECT_ID(N'dbo.'+@Trigger,N'TR') IS NOT NULL AND @Definition IS NULL
                THROW 50230,'Existing goods issue trigger definition is unreadable. Review before migration.',1;
            IF @Definition IS NOT NULL
            BEGIN
                SET @Canonical=UPPER(REPLACE(REPLACE(REPLACE(REPLACE(@Definition,N' ',N''),CHAR(13),N''),CHAR(10),N''),CHAR(9),N''));
                SET @Canonical=REPLACE(REPLACE(@Canonical,N'CREATEORALTERTRIGGER',N'CREATETRIGGER'),N'ALTERTRIGGER',N'CREATETRIGGER');
                SET @Hash=HASHBYTES('SHA2_256',@Canonical);
                IF (@Trigger=N'TR_INVENTORY_RESERVATION_SYNC' AND @Hash NOT IN (0xE11ED450E13BFDCD7EEAA314DB8DB7B6559FF9D402501E46E2186ABB462A7639,0x672409D8227C77AE08C82C5DD6BCD6C7F22A1D6F419E45BDF5E8160CA93A4F70))
                    OR (@Trigger=N'TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK' AND @Hash NOT IN (0xE9F1D246F1C8D818A823D2793C501CB93E7E5CAB220D023AB7C9A543F39D3289,0x2E9CD96975B291E586B49B54ADDF38319141A161F77969A7E758D27B566FD7A1))
                    THROW 50230,'Goods issue trigger differs from verified baseline/V1. Review local changes before migration.',1;
            END;
            FETCH NEXT FROM known INTO @Trigger;
        END;
        CLOSE known;
        DEALLOCATE known;
        """;

    private const string ReservationSyncV1 = """
        CREATE OR ALTER TRIGGER dbo.TR_INVENTORY_RESERVATION_SYNC
        ON dbo.INVENTORY_RESERVATION AFTER INSERT, UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            -- SAW_GOODS_ISSUE_V1: a committed partial issue consumes only its own held kg.
            ;WITH Affected AS (SELECT InventoryID FROM inserted UNION SELECT InventoryID FROM deleted),
            Issued AS (
                SELECT d.InventoryReservationID,SUM(d.WeightInKg) AS Kg
                FROM dbo.GOODS_ISSUE_DETAIL d JOIN dbo.GOODS_ISSUE g ON g.GoodsIssueID=d.GoodsIssueID
                WHERE g.IssueStatus=N'COMMITTED' GROUP BY d.InventoryReservationID
            )
            UPDATE inv SET ReservedQuantity=ISNULL((SELECT SUM(r.ReservedQuantity-ISNULL(s.Kg,0))
                FROM dbo.INVENTORY_RESERVATION r LEFT JOIN Issued s ON s.InventoryReservationID=r.InventoryReservationID
                WHERE r.InventoryID=inv.InventoryID AND r.ReservationStatus IN (N'RESERVED',N'PARTIALLY_PICKED',N'PICKED')),0),
                LastUpdatedAt=SYSUTCDATETIME()
            FROM dbo.INVENTORY inv JOIN Affected a ON a.InventoryID=inv.InventoryID;
        
            -- OrderDetail records cumulative allocated/picked kg, not the inventory's remaining hold.
            -- Keep issued allocations here to preserve PickedWeightKg <= ReservedWeightKg and history.
            ;WITH Affected AS (SELECT OrderDetailID FROM inserted UNION SELECT OrderDetailID FROM deleted)
            UPDATE od SET ReservedWeightKg=ISNULL((SELECT SUM(r.ReservedQuantity)
                FROM dbo.INVENTORY_RESERVATION r
                WHERE r.OrderDetailID=od.OrderDetailID AND r.ReservationStatus IN (N'RESERVED',N'PARTIALLY_PICKED',N'PICKED',N'ISSUED')),0),
                PickedWeightKg=ISNULL((SELECT SUM(r.PickedQuantity) FROM dbo.INVENTORY_RESERVATION r
                    WHERE r.OrderDetailID=od.OrderDetailID AND r.ReservationStatus NOT IN (N'RELEASED',N'CANCELLED')),0)
            FROM dbo.ORDER_DETAIL od JOIN Affected a ON a.OrderDetailID=od.OrderDetailID;
        END;
        """;

    private const string IssueDetailIntegrityV1 = """
        CREATE OR ALTER TRIGGER dbo.TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK
        ON dbo.GOODS_ISSUE_DETAIL AFTER INSERT, UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            -- SAW_GOODS_ISSUE_V1: drafts are alternatives, not inventory transactions.
            IF EXISTS (SELECT 1 FROM (SELECT GoodsIssueID FROM inserted UNION SELECT GoodsIssueID FROM deleted) a
                JOIN dbo.GOODS_ISSUE g ON g.GoodsIssueID=a.GoodsIssueID WHERE g.IssueStatus=N'COMMITTED')
                THROW 50112,'Cannot change details of a COMMITTED Goods Issue.',1;
            IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.INVENTORY_RESERVATION r ON r.InventoryReservationID=i.InventoryReservationID
                JOIN dbo.ORDER_DETAIL od ON od.OrderDetailID=r.OrderDetailID JOIN dbo.GOODS_ISSUE g ON g.GoodsIssueID=i.GoodsIssueID
                WHERE i.OrderDetailID<>r.OrderDetailID OR i.InventoryID<>r.InventoryID OR g.PurchaseOrderID<>od.PurchaseOrderID)
                THROW 50113,'Goods Issue order/reservation/inventory mismatch.',1;
            IF EXISTS (
                SELECT 1 FROM (SELECT DISTINCT GoodsIssueID FROM inserted) a
                JOIN dbo.GOODS_ISSUE_DETAIL d ON d.GoodsIssueID=a.GoodsIssueID
                JOIN dbo.INVENTORY_RESERVATION r ON r.InventoryReservationID=d.InventoryReservationID
                GROUP BY d.GoodsIssueID,d.InventoryReservationID,r.PickedQuantity
                HAVING SUM(d.WeightInKg)>r.PickedQuantity-ISNULL((SELECT SUM(x.WeightInKg)
                    FROM dbo.GOODS_ISSUE_DETAIL x JOIN dbo.GOODS_ISSUE g ON g.GoodsIssueID=x.GoodsIssueID
                    WHERE x.InventoryReservationID=d.InventoryReservationID AND g.IssueStatus=N'COMMITTED'),0))
                THROW 50119,'Issue weight exceeds picked weight remaining after committed issues.',1;
            IF EXISTS (
                SELECT 1 FROM (SELECT DISTINCT GoodsIssueID FROM inserted) a
                JOIN dbo.GOODS_ISSUE_DETAIL d ON d.GoodsIssueID=a.GoodsIssueID
                JOIN dbo.ORDER_DETAIL od ON od.OrderDetailID=d.OrderDetailID
                GROUP BY d.GoodsIssueID,d.OrderDetailID,od.ApprovedWeightKg
                HAVING SUM(d.WeightInKg)>od.ApprovedWeightKg-ISNULL((SELECT SUM(x.WeightInKg)
                    FROM dbo.GOODS_ISSUE_DETAIL x JOIN dbo.GOODS_ISSUE g ON g.GoodsIssueID=x.GoodsIssueID
                    WHERE x.OrderDetailID=d.OrderDetailID AND g.IssueStatus=N'COMMITTED'),0))
                THROW 50120,'Issue weight exceeds approved line weight.',1;
        END;
        """;

    private const string ReservationSyncLegacy = """
        /* SYNCHRONIZE RESERVATION COUNTERS (STANDARDIZED ON WEIGHT KG) */
        CREATE OR ALTER TRIGGER dbo.TR_INVENTORY_RESERVATION_SYNC
        ON dbo.INVENTORY_RESERVATION
        AFTER INSERT, UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
        
            ;WITH AffectedInventory AS (
                SELECT InventoryID FROM inserted
                UNION
                SELECT InventoryID FROM deleted
            )
            UPDATE inv
               SET ReservedQuantity =
                    ISNULL((
                        SELECT SUM(r.ReservedQuantity)
                        FROM dbo.INVENTORY_RESERVATION r
                        WHERE r.InventoryID = inv.InventoryID
                          AND r.ReservationStatus IN
                              (N'RESERVED', N'PARTIALLY_PICKED', N'PICKED')
                    ), 0),
                   LastUpdatedAt = SYSDATETIME()
            FROM dbo.INVENTORY inv
            JOIN AffectedInventory a
              ON a.InventoryID = inv.InventoryID;
        
            ;WITH AffectedOrderDetail AS (
                SELECT OrderDetailID FROM inserted
                UNION
                SELECT OrderDetailID FROM deleted
            )
            UPDATE od
               SET ReservedWeightKg =
                    ISNULL((
                        SELECT SUM(r.ReservedQuantity)
                        FROM dbo.INVENTORY_RESERVATION r
                        WHERE r.OrderDetailID = od.OrderDetailID
                          AND r.ReservationStatus IN
                              (N'RESERVED', N'PARTIALLY_PICKED', N'PICKED')
                    ), 0),
                   PickedWeightKg =
                    ISNULL((
                        SELECT SUM(r.PickedQuantity)
                        FROM dbo.INVENTORY_RESERVATION r
                        WHERE r.OrderDetailID = od.OrderDetailID
                          AND r.ReservationStatus NOT IN (N'RELEASED', N'CANCELLED')
                    ), 0)
            FROM dbo.ORDER_DETAIL od
            JOIN AffectedOrderDetail a
              ON a.OrderDetailID = od.OrderDetailID;
        END;
        """;

    private const string IssueDetailIntegrityLegacy = """
        /* GOODS ISSUE DETAIL IMMUTABILITY & EXHAUSTIVE QUANTITY/ORDER BOUNDS */
        CREATE OR ALTER TRIGGER dbo.TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK
        ON dbo.GOODS_ISSUE_DETAIL
        AFTER INSERT, UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
        
            -- Block modifications if parent Goods Issue is committed
            IF EXISTS (
                SELECT 1
                FROM (SELECT GoodsIssueID FROM inserted UNION SELECT GoodsIssueID FROM deleted) a
                JOIN dbo.GOODS_ISSUE gi ON gi.GoodsIssueID = a.GoodsIssueID
                WHERE gi.IssueStatus = N'COMMITTED'
            )
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50112, 'Cannot insert, modify or delete issue details for a COMMITTED Goods Issue.', 1;
            END
        
            -- Integrity Check 1: OrderDetail, Reservation & Inventory must belong to the exact same reservation
            IF EXISTS (
                SELECT 1
                FROM inserted i
                JOIN dbo.INVENTORY_RESERVATION r ON r.InventoryReservationID = i.InventoryReservationID
                WHERE r.OrderDetailID <> i.OrderDetailID
                   OR r.InventoryID <> i.InventoryID
            )
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50113, 'Integrity mismatch: GoodsIssueDetail elements do not belong to the same reservation allocation.', 1;
            END
        
            -- Integrity Check 2: GoodsIssue.PurchaseOrderID must match the OrderDetail.PurchaseOrderID
            IF EXISTS (
                SELECT 1
                FROM inserted i
                JOIN dbo.GOODS_ISSUE gi ON gi.GoodsIssueID = i.GoodsIssueID
                JOIN dbo.ORDER_DETAIL od ON od.OrderDetailID = i.OrderDetailID
                WHERE gi.PurchaseOrderID <> od.PurchaseOrderID
            )
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50118, 'Order Mismatch: GoodsIssue parent PurchaseOrderID does not match line item PurchaseOrderID.', 1;
            END
        
            -- Integrity Check 3: Issued weight cannot exceed the picked weight of that reservation
            IF EXISTS (
                SELECT 1
                FROM inserted i
                JOIN dbo.INVENTORY_RESERVATION r ON r.InventoryReservationID = i.InventoryReservationID
                JOIN (
                    SELECT InventoryReservationID, SUM(WeightInKg) AS TotalIssuedWeightKg
                    FROM dbo.GOODS_ISSUE_DETAIL
                    GROUP BY InventoryReservationID
                ) gid_sum ON gid_sum.InventoryReservationID = r.InventoryReservationID
                WHERE gid_sum.TotalIssuedWeightKg > r.PickedQuantity
            )
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50119, 'Quantity Violation: Total issued weight (kg) cannot exceed the confirmed picked quantity (kg).', 1;
            END
        END;
        """;
}
