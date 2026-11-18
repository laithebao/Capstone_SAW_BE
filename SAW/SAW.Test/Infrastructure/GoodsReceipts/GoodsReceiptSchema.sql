-- Verified warehouse trigger definitions, versioned with the tests.
-- Install only in a GUID-named fixture database.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
CREATE OR ALTER TRIGGER dbo.TR_GOODS_RECEIPT_COMMITTED_IMMUTABLE
ON dbo.GOODS_RECEIPT
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM deleted
        WHERE ReceiptStatus = N'COMMITTED'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50106, 'Committed Goods Receipt cannot be modified or deleted.', 1;
    END
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_PRODUCT_BATCH_STATUS_HISTORY
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
CREATE OR ALTER TRIGGER dbo.TR_BATCH_STATUS_HISTORY_IMMUTABLE
ON dbo.BATCH_STATUS_HISTORY
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    THROW 50103, 'BATCH_STATUS_HISTORY records are immutable.', 1;
END;
GO
