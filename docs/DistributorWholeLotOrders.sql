BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007051407_AddDistributorWholeLotOrders'
)
BEGIN
    ALTER TABLE [PURCHASE_ORDER] ADD [ReceivedAt] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007051407_AddDistributorWholeLotOrders'
)
BEGIN
    ALTER TABLE [PURCHASE_ORDER] ADD [ReceivedByAccountID] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007051407_AddDistributorWholeLotOrders'
)
BEGIN
    CREATE TABLE [BATCH_SALE_OFFER] (
        [ProductBatchID] bigint NOT NULL,
        [WholeLotPrice] decimal(18,2) NOT NULL,
        [IsPublished] bit NOT NULL,
        [UpdatedAt] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_BATCH_SALE_OFFER] PRIMARY KEY ([ProductBatchID]),
        CONSTRAINT [CK_BATCH_SALE_OFFER_Price] CHECK ([WholeLotPrice] > 0),
        CONSTRAINT [FK_BATCH_SALE_OFFER_PRODUCT_BATCH_ProductBatchID] FOREIGN KEY ([ProductBatchID]) REFERENCES [PRODUCT_BATCH] ([ProductBatchID]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007051407_AddDistributorWholeLotOrders'
)
BEGIN
    CREATE INDEX [IX_PURCHASE_ORDER_ReceivedByAccountID] ON [PURCHASE_ORDER] ([ReceivedByAccountID]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007051407_AddDistributorWholeLotOrders'
)
BEGIN
    ALTER TABLE [PURCHASE_ORDER] ADD CONSTRAINT [FK_PURCHASE_ORDER_ACCOUNT_ReceivedByAccountID] FOREIGN KEY ([ReceivedByAccountID]) REFERENCES [ACCOUNT] ([AccountID]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007051407_AddDistributorWholeLotOrders'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007051407_AddDistributorWholeLotOrders', N'8.0.0');
END;
GO

COMMIT;
GO

