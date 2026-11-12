-- Test-only warehouse schema snapshot, reviewed 2026-11-12.
-- Same trigger/check definitions used by the passing warehouse fixture.
-- Applied only to the GUID-named database created by ReceivingSqlFixture.
-- Keep this baseline versioned; workflow trigger updates are applied from EF migration SQL.
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_GOODS_ISSUE_DETAIL_Quantity') ALTER TABLE [dbo].[GOODS_ISSUE_DETAIL] ADD CONSTRAINT [CK_GOODS_ISSUE_DETAIL_Quantity] CHECK ([IssuedQuantity]>(0) AND [WeightInKg]>(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_GOODS_ISSUE_Status') ALTER TABLE [dbo].[GOODS_ISSUE] ADD CONSTRAINT [CK_GOODS_ISSUE_Status] CHECK ([IssueStatus]=N'COMMITTED' OR [IssueStatus]=N'DRAFT')
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_Grade') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_Grade] CHECK ([QualityGrade] IS NULL OR ([QualityGrade]=N'E' OR [QualityGrade]=N'D' OR [QualityGrade]=N'C' OR [QualityGrade]=N'B' OR [QualityGrade]=N'A'))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_HarvestExpiry') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_HarvestExpiry] CHECK ([ExpiryDate] IS NULL OR [HarvestDate]<=[ExpiryDate])
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_Humidity') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_Humidity] CHECK (([ExpectedMinHumidityPct] IS NULL OR [ExpectedMinHumidityPct]>=(0) AND [ExpectedMinHumidityPct]<=(100)) AND ([ExpectedMaxHumidityPct] IS NULL OR [ExpectedMaxHumidityPct]>=(0) AND [ExpectedMaxHumidityPct]<=(100)) AND ([ExpectedMinHumidityPct] IS NULL OR [ExpectedMaxHumidityPct] IS NULL OR [ExpectedMinHumidityPct]<=[ExpectedMaxHumidityPct]))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_PackageCount') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_PackageCount] CHECK ([PackageCount] IS NULL OR [PackageCount]>(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_PackageUnitWeight') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_PackageUnitWeight] CHECK ([PackageUnitWeightKg] IS NULL OR [PackageUnitWeightKg]>(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_Quantity') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_Quantity] CHECK ([DeclaredQuantity]>(0) AND [WeightInKg]>(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_ShelfLife') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_ShelfLife] CHECK ([ShelfLifeDaysSnapshot] IS NULL OR [ShelfLifeDaysSnapshot]>(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_Status') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_Status] CHECK ([BatchStatus]=N'CANCELLED' OR [BatchStatus]=N'ISSUED' OR [BatchStatus]=N'PARTIALLY_ISSUED' OR [BatchStatus]=N'RESERVED' OR [BatchStatus]=N'IN_STOCK' OR [BatchStatus]=N'RECEIVED' OR [BatchStatus]=N'REJECTED' OR [BatchStatus]=N'QUARANTINE' OR [BatchStatus]=N'APPROVED_FOR_STORAGE' OR [BatchStatus]=N'PENDING_QC' OR [BatchStatus]=N'SUBMITTED' OR [BatchStatus]=N'PENDING_PREDECLARATION' OR [BatchStatus]=N'DISPOSED')
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_Temperature') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_Temperature] CHECK ([ExpectedMinTempC] IS NULL OR [ExpectedMaxTempC] IS NULL OR [ExpectedMinTempC]<=[ExpectedMaxTempC])
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_VerifiedPackageCount') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_VerifiedPackageCount] CHECK ([VerifiedPackageCount] IS NULL OR [VerifiedPackageCount]>(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_PRODUCT_BATCH_VerifiedPackageUnitWeightKg') ALTER TABLE [dbo].[PRODUCT_BATCH] ADD CONSTRAINT [CK_PRODUCT_BATCH_VerifiedPackageUnitWeightKg] CHECK ([VerifiedPackageUnitWeightKg] IS NULL OR [VerifiedPackageUnitWeightKg]>(0))
GO
/* COMMITTED ISSUES IMMUTABLE (BR-29) */
CREATE   TRIGGER dbo.TR_GOODS_ISSUE_COMMITTED_IMMUTABLE
ON dbo.GOODS_ISSUE
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM deleted
        WHERE IssueStatus = N'COMMITTED'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50107, 'Committed Goods Issue cannot be modified or deleted.', 1;
    END
END;
GO
CREATE   TRIGGER dbo.TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK
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
GO
