-- Test-only warehouse schema snapshot, reviewed 2026-11-12.
-- Same trigger/check definitions used by the passing warehouse fixture.
-- Applied only to the GUID-named database created by ReceivingSqlFixture.
-- Keep this baseline versioned; workflow trigger updates are applied from EF migration SQL.
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_AUDIT_LOG_NewJson') ALTER TABLE [dbo].[AUDIT_LOG] ADD CONSTRAINT [CK_AUDIT_LOG_NewJson] CHECK ([NewDataJson] IS NULL OR isjson([NewDataJson])=(1))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_AUDIT_LOG_OldJson') ALTER TABLE [dbo].[AUDIT_LOG] ADD CONSTRAINT [CK_AUDIT_LOG_OldJson] CHECK ([OldDataJson] IS NULL OR isjson([OldDataJson])=(1))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_INVENTORY_Quantity') ALTER TABLE [dbo].[INVENTORY] ADD CONSTRAINT [CK_INVENTORY_Quantity] CHECK ([QuantityOnHand]>=(0) AND [ReservedQuantity]>=(0) AND [ReservedQuantity]<=[QuantityOnHand])
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_INVENTORY_RESERVATION_Quantity') ALTER TABLE [dbo].[INVENTORY_RESERVATION] ADD CONSTRAINT [CK_INVENTORY_RESERVATION_Quantity] CHECK ([ReservedQuantity]>(0) AND [PickedQuantity]>=(0) AND [PickedQuantity]<=[ReservedQuantity])
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_INVENTORY_RESERVATION_Status') ALTER TABLE [dbo].[INVENTORY_RESERVATION] ADD CONSTRAINT [CK_INVENTORY_RESERVATION_Status] CHECK ([ReservationStatus]=N'CANCELLED' OR [ReservationStatus]=N'ISSUED' OR [ReservationStatus]=N'RELEASED' OR [ReservationStatus]=N'PICKED' OR [ReservationStatus]=N'PARTIALLY_PICKED' OR [ReservationStatus]=N'RESERVED')
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_INVENTORY_Unit') ALTER TABLE [dbo].[INVENTORY] ADD CONSTRAINT [CK_INVENTORY_Unit] CHECK ([Unit]=N'kg')
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_ORDER_DETAIL_Money') ALTER TABLE [dbo].[ORDER_DETAIL] ADD CONSTRAINT [CK_ORDER_DETAIL_Money] CHECK ([UnitPrice]>=(0) AND [TaxAmount]>=(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_ORDER_DETAIL_Quantity') ALTER TABLE [dbo].[ORDER_DETAIL] ADD CONSTRAINT [CK_ORDER_DETAIL_Quantity] CHECK ([RequestedQuantity]>(0) AND [ApprovedQuantity]>=(0) AND [ApprovedQuantity]<=[RequestedQuantity])
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_ORDER_DETAIL_Weight') ALTER TABLE [dbo].[ORDER_DETAIL] ADD CONSTRAINT [CK_ORDER_DETAIL_Weight] CHECK ([RequestedWeightKg]>(0) AND [ApprovedWeightKg]>=(0) AND [ApprovedWeightKg]<=[RequestedWeightKg] AND [ReservedWeightKg]>=(0) AND [ReservedWeightKg]<=[ApprovedWeightKg] AND [PickedWeightKg]>=(0) AND [PickedWeightKg]<=[ReservedWeightKg])
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PICKING_HISTORY_Quantity') ALTER TABLE [dbo].[PICKING_HISTORY] ADD CONSTRAINT [CK_PICKING_HISTORY_Quantity] CHECK ([PickedQuantity]>(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PURCHASE_ORDER_Amounts') ALTER TABLE [dbo].[PURCHASE_ORDER] ADD CONSTRAINT [CK_PURCHASE_ORDER_Amounts] CHECK ([SubtotalAmount]>=(0) AND [TaxAmount]>=(0) AND [TotalAmount]>=(0) AND [TotalAmount]=([SubtotalAmount]+[TaxAmount]))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PURCHASE_ORDER_Status') ALTER TABLE [dbo].[PURCHASE_ORDER] ADD CONSTRAINT [CK_PURCHASE_ORDER_Status] CHECK ([OrderStatus]='RETURNED' OR [OrderStatus]='CANCELLED' OR [OrderStatus]='REJECTED' OR [OrderStatus]='DELIVERED' OR [OrderStatus]='DISPATCHED' OR [OrderStatus]='ISSUED' OR [OrderStatus]='PARTIALLY_ISSUED' OR [OrderStatus]='PREPARED' OR [OrderStatus]='PICKED' OR [OrderStatus]='PARTIALLY_PICKED' OR [OrderStatus]='RESERVED' OR [OrderStatus]='APPROVED' OR [OrderStatus]='PENDING' OR [OrderStatus]=N'DELIVERY_REFUSED' OR [OrderStatus]=N'PARTIALLY_REFUSED')
GO
/*==============================================================================
 12. IMMUTABILITY, INTEGRITY & AUTO-SYNC TRIGGERS
==============================================================================*/

CREATE   TRIGGER dbo.TR_AUDIT_LOG_IMMUTABLE
ON dbo.AUDIT_LOG
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    THROW 50100, 'AUDIT_LOG records are immutable.', 1;
END;
GO
CREATE   TRIGGER dbo.TR_BATCH_STATUS_HISTORY_IMMUTABLE
ON dbo.BATCH_STATUS_HISTORY
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    THROW 50103, 'BATCH_STATUS_HISTORY records are immutable.', 1;
END;
GO
/* CROSS-ENTITY RESERVATION INTEGRITY CHECK */
CREATE   TRIGGER dbo.TR_INVENTORY_RESERVATION_INTEGRITY
ON dbo.INVENTORY_RESERVATION
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Validate CropType match and batch specification match
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN dbo.ORDER_DETAIL od ON od.OrderDetailID = i.OrderDetailID
        JOIN dbo.INVENTORY inv ON inv.InventoryID = i.InventoryID
        JOIN dbo.PRODUCT_BATCH pb ON pb.ProductBatchID = inv.ProductBatchID
        WHERE pb.CropTypeID <> od.CropTypeID
           OR (od.RequestedProductBatchID IS NOT NULL AND od.RequestedProductBatchID <> pb.ProductBatchID)
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50110, 'Reservation violation: Batch crop type does not match order detail specification.', 1;
    END

    -- Validate that total active reservation for line item does not exceed ApprovedWeightKg
    IF EXISTS (
        SELECT 1
        FROM dbo.ORDER_DETAIL od
        JOIN (
            SELECT r.OrderDetailID, SUM(r.ReservedQuantity) AS TotalReservedKg
            FROM dbo.INVENTORY_RESERVATION r
            WHERE r.ReservationStatus IN (N'RESERVED', N'PARTIALLY_PICKED', N'PICKED')
            GROUP BY r.OrderDetailID
        ) r_sum ON r_sum.OrderDetailID = od.OrderDetailID
        WHERE r_sum.TotalReservedKg > od.ApprovedWeightKg
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50111, 'Reservation violation: Total reserved weight (kg) cannot exceed approved weight (kg).', 1;
    END
END;
GO
CREATE   TRIGGER dbo.TR_INVENTORY_RESERVATION_SYNC
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
GO
CREATE   TRIGGER dbo.TR_ORDER_STATUS_HISTORY_IMMUTABLE
ON dbo.ORDER_STATUS_HISTORY
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    THROW 50104, 'ORDER_STATUS_HISTORY records are immutable.', 1;
END;
GO
CREATE   TRIGGER dbo.TR_PICKING_HISTORY_IMMUTABLE
ON dbo.PICKING_HISTORY
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    THROW 50102, 'PICKING_HISTORY records are immutable.', 1;
END;
GO
CREATE   TRIGGER dbo.TR_PICKING_HISTORY_SYNC
ON dbo.PICKING_HISTORY
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH Affected AS (
        SELECT DISTINCT InventoryReservationID
        FROM inserted
    ),
    Totals AS (
        SELECT
            r.InventoryReservationID,
            r.ReservedQuantity,
            ISNULL(SUM(ph.PickedQuantity), 0) AS TotalPicked
        FROM dbo.INVENTORY_RESERVATION r
        JOIN Affected a
          ON a.InventoryReservationID = r.InventoryReservationID
        LEFT JOIN dbo.PICKING_HISTORY ph
          ON ph.InventoryReservationID = r.InventoryReservationID
        GROUP BY
            r.InventoryReservationID,
            r.ReservedQuantity
    )
    UPDATE r
       SET PickedQuantity = t.TotalPicked,
           ReservationStatus =
                CASE
                    WHEN t.TotalPicked = 0
                        THEN N'RESERVED'
                    WHEN t.TotalPicked < t.ReservedQuantity
                        THEN N'PARTIALLY_PICKED'
                    ELSE N'PICKED'
                END
    FROM dbo.INVENTORY_RESERVATION r
    JOIN Totals t
      ON t.InventoryReservationID = r.InventoryReservationID;

    IF EXISTS (
        SELECT 1
        FROM dbo.INVENTORY_RESERVATION r
        JOIN inserted i
          ON i.InventoryReservationID = r.InventoryReservationID
        WHERE r.PickedQuantity > r.ReservedQuantity
    )
    BEGIN
        THROW 50105, 'Picked quantity cannot exceed reserved quantity.', 1;
    END
END;
GO
CREATE   TRIGGER dbo.TR_PRODUCT_BATCH_STATUS_HISTORY
ON dbo.PRODUCT_BATCH
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.BATCH_STATUS_HISTORY
        (ProductBatchID, OldStatus, NewStatus, ChangedByAccountID, ChangedAt)
    SELECT
        i.ProductBatchID,
        d.BatchStatus,
        i.BatchStatus,
        TRY_CONVERT(INT, SESSION_CONTEXT(N'AccountID')),
        SYSDATETIME()
    FROM inserted i
    LEFT JOIN deleted d
        ON d.ProductBatchID = i.ProductBatchID
    WHERE d.ProductBatchID IS NULL
       OR ISNULL(d.BatchStatus, N'') <> ISNULL(i.BatchStatus, N'');
END;
GO
CREATE   TRIGGER dbo.TR_PURCHASE_ORDER_STATUS_HISTORY
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
GO
