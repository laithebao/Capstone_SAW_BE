-- Run after DistributorWholeLotOrders.sql, in SmartAgriWarehouseDB.
-- Supply the actual batch ID and agreed WHOLE LOT price in VND. No per-kg price.
DECLARE @BatchID bigint = NULL;
DECLARE @WholeLotPrice decimal(18,2) = NULL;
DECLARE @IsPublished bit = 1;

IF DB_NAME() <> N'SmartAgriWarehouseDB'
    THROW 50200, 'Select SmartAgriWarehouseDB before running this script.', 1;
IF @BatchID IS NULL OR @BatchID <= 0 OR @WholeLotPrice IS NULL OR @WholeLotPrice <= 0
    THROW 50201, 'Enter a batch ID and a positive whole-lot price first.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.PRODUCT_BATCH WHERE ProductBatchID = @BatchID)
    THROW 50202, 'Batch not found.', 1;

SET XACT_ABORT ON;
BEGIN TRANSACTION;
UPDATE dbo.BATCH_SALE_OFFER WITH (UPDLOCK, SERIALIZABLE)
SET WholeLotPrice = @WholeLotPrice, IsPublished = @IsPublished, UpdatedAt = SYSUTCDATETIME()
WHERE ProductBatchID = @BatchID;
IF @@ROWCOUNT = 0
    INSERT dbo.BATCH_SALE_OFFER (ProductBatchID, WholeLotPrice, IsPublished, UpdatedAt)
    VALUES (@BatchID, @WholeLotPrice, @IsPublished, SYSUTCDATETIME());
COMMIT;
