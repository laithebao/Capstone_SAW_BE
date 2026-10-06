-- Upgrade only an existing current SAW database, not an empty database.
-- InitialCreate is an empty baseline. Do not replay older migrations from this script.
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004171724_AddSupplierFiles'
)
BEGIN
    CREATE TABLE [SUPPLIER_FILE] (
        [SupplierFileId] uniqueidentifier NOT NULL,
        [AccountId] int NOT NULL,
        [SupplierId] int NULL,
        [ProductBatchId] bigint NULL,
        [Purpose] nvarchar(20) NOT NULL,
        [FileName] nvarchar(255) NOT NULL,
        [ContentType] nvarchar(100) NOT NULL,
        [StorageName] nvarchar(100) NOT NULL,
        [ByteLength] bigint NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_SUPPLIER_FILE] PRIMARY KEY ([SupplierFileId]),
        CONSTRAINT [FK_SUPPLIER_FILE_ACCOUNT_AccountId] FOREIGN KEY ([AccountId]) REFERENCES [ACCOUNT] ([AccountID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SUPPLIER_FILE_PRODUCT_BATCH_ProductBatchId] FOREIGN KEY ([ProductBatchId]) REFERENCES [PRODUCT_BATCH] ([ProductBatchID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SUPPLIER_FILE_SUPPLIER_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [SUPPLIER] ([SupplierID]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004171724_AddSupplierFiles'
)
BEGIN
    CREATE INDEX [IX_SUPPLIER_FILE_AccountId] ON [SUPPLIER_FILE] ([AccountId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004171724_AddSupplierFiles'
)
BEGIN
    CREATE INDEX [IX_SUPPLIER_FILE_ProductBatchId] ON [SUPPLIER_FILE] ([ProductBatchId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004171724_AddSupplierFiles'
)
BEGIN
    CREATE INDEX [IX_SUPPLIER_FILE_SupplierId_ProductBatchId] ON [SUPPLIER_FILE] ([SupplierId], [ProductBatchId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004171724_AddSupplierFiles'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261004171724_AddSupplierFiles', N'8.0.0');
END;
GO

COMMIT;
GO

