/*
    Seed listing prices for existing warehouse-created lots.
    Does not create accounts, product batches, inventory, QC records, orders or schema.

    1. Apply the Distributor Code First migrations first.
    2. Select the application's database in SSMS.
    3. Replace the example batch codes and prices with existing warehouse lot codes
       and agreed whole-lot prices, then execute this whole script.

    Only lots that meet the Distributor catalog's current stock/QC rules are published.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.BATCH_SALE_OFFER', N'U') IS NULL
    THROW 50400, 'Apply the Distributor Code First migrations before seeding lot prices.', 1;

DECLARE @Lots TABLE
(
    BatchCode nvarchar(50) NOT NULL PRIMARY KEY,
    WholeLotPrice decimal(18,2) NOT NULL
);

-- Replace these example codes with existing lots created/received by the warehouse.
-- Prices are for the WHOLE lot, not per kilogram.
INSERT @Lots (BatchCode, WholeLotPrice) VALUES
    (N'DEMO-DIST-LOT-01', 1500000.00),
    (N'DEMO-DIST-LOT-02', 2400000.00),
    (N'DEMO-DIST-LOT-03', 1200000.00);

IF EXISTS (SELECT 1 FROM @Lots WHERE WholeLotPrice <= 0)
    THROW 50401, 'Every whole-lot price must be greater than zero.', 1;
IF EXISTS
(
    SELECT 1 FROM @Lots l
    LEFT JOIN dbo.PRODUCT_BATCH b ON b.BatchCode = l.BatchCode
    WHERE b.ProductBatchID IS NULL
)
    THROW 50402, 'One or more batch codes do not exist. Use existing warehouse-created lot codes.', 1;

DECLARE @Today date = CONVERT(date, DATEADD(hour, 7, SYSUTCDATETIME()));
DECLARE @NotEligible TABLE (BatchCode nvarchar(50) NOT NULL);

INSERT @NotEligible (BatchCode)
SELECT l.BatchCode
FROM @Lots l
JOIN dbo.PRODUCT_BATCH b ON b.BatchCode = l.BatchCode
WHERE b.BatchStatus <> N'IN_STOCK'
   OR b.CropTypeID IS NULL
   OR NOT EXISTS (SELECT 1 FROM dbo.CROP_TYPE c WHERE c.CropTypeID=b.CropTypeID AND c.IsActive=1)
   OR (b.ExpiryDate IS NOT NULL AND b.ExpiryDate < @Today)
   OR b.VerifiedWeightInKg IS NULL OR b.VerifiedWeightInKg <= 0
   OR NOT EXISTS (SELECT 1 FROM dbo.INVENTORY i WHERE i.ProductBatchID=b.ProductBatchID)
   OR EXISTS
      (SELECT 1 FROM dbo.INVENTORY i
       JOIN dbo.WAREHOUSE_LOCATION w ON w.WarehouseLocationID=i.WarehouseLocationID
       WHERE i.ProductBatchID=b.ProductBatchID
         AND (i.ReservedQuantity <> 0 OR i.Unit <> N'kg' OR w.LocationStatus <> N'ACTIVE'))
   OR (SELECT SUM(i.QuantityOnHand) FROM dbo.INVENTORY i WHERE i.ProductBatchID=b.ProductBatchID) <> b.VerifiedWeightInKg
   OR EXISTS
      (SELECT 1 FROM dbo.GOODS_ISSUE_DETAIL d
       JOIN dbo.GOODS_ISSUE g ON g.GoodsIssueID=d.GoodsIssueID
       JOIN dbo.INVENTORY i ON i.InventoryID=d.InventoryID
       WHERE i.ProductBatchID=b.ProductBatchID AND g.IssueStatus=N'COMMITTED')
   OR NOT EXISTS
      (SELECT 1
       FROM (SELECT TOP (1) q.InspectionStatus,q.CompletedAt,q.StartedAt,q.QCResult,q.QualityGrade
             FROM dbo.QC_INSPECTION q
             WHERE q.ProductBatchID=b.ProductBatchID
             ORDER BY q.StartedAt DESC,q.QCInspectionID DESC) latest
       WHERE latest.InspectionStatus=N'COMPLETED'
         AND latest.CompletedAt >= latest.StartedAt
         AND latest.QCResult=N'PASS'
         AND latest.QualityGrade=b.QualityGrade
         AND latest.QualityGrade IN (N'A',N'B',N'C',N'D'));

IF EXISTS (SELECT 1 FROM @NotEligible)
BEGIN
    SELECT BatchCode, N'Chưa đủ điều kiện bán: kiểm tra trạng thái, hạn dùng, QC và tồn kho.' AS Issue
    FROM @NotEligible;
    THROW 50403, 'Some lots are not eligible for the Distributor catalog. No prices were changed.', 1;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    UPDATE offer WITH (UPDLOCK, SERIALIZABLE)
       SET WholeLotPrice = l.WholeLotPrice,
           IsPublished = 1,
           UpdatedAt = SYSUTCDATETIME()
    FROM dbo.BATCH_SALE_OFFER offer
    JOIN dbo.PRODUCT_BATCH b ON b.ProductBatchID=offer.ProductBatchID
    JOIN @Lots l ON l.BatchCode=b.BatchCode;

    INSERT dbo.BATCH_SALE_OFFER (ProductBatchID,WholeLotPrice,IsPublished,UpdatedAt)
    SELECT b.ProductBatchID,l.WholeLotPrice,1,SYSUTCDATETIME()
    FROM @Lots l
    JOIN dbo.PRODUCT_BATCH b ON b.BatchCode=l.BatchCode
    WHERE NOT EXISTS
      (SELECT 1 FROM dbo.BATCH_SALE_OFFER offer WITH (UPDLOCK,SERIALIZABLE)
       WHERE offer.ProductBatchID=b.ProductBatchID);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT b.BatchCode,b.ProductName,b.VerifiedWeightInKg AS WholeLotWeightKg,
       offer.WholeLotPrice,offer.IsPublished
FROM @Lots l
JOIN dbo.PRODUCT_BATCH b ON b.BatchCode=l.BatchCode
JOIN dbo.BATCH_SALE_OFFER offer ON offer.ProductBatchID=b.ProductBatchID
ORDER BY b.BatchCode;
