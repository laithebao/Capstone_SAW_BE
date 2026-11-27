namespace SAW.Infrastructure.Migrations;

internal static class UtcTimestampTriggerSql
{
    // Preserve the actual environment's trigger body for guarded rollback; only
    // replace its clock function. No status/history rows are updated.
    internal const string Up = """
        DECLARE @id int=OBJECT_ID(N'dbo.TR_PRODUCT_BATCH_STATUS_HISTORY', N'TR');
        IF @id IS NOT NULL
        BEGIN
            DECLARE @old nvarchar(max)=OBJECT_DEFINITION(@id), @next nvarchar(max), @disabled bit;
            SELECT @disabled=is_disabled FROM sys.triggers WHERE object_id=@id;
            IF @old IS NULL THROW 51210, 'Cannot read batch history trigger; review before UTC migration.', 1;
            IF CHARINDEX(N'SYSDATETIME', UPPER(@old))>0
            BEGIN
                IF DATALENGTH(@old)>7500 THROW 51210, 'Batch history trigger is too large for safe rollback metadata.', 1;
                IF EXISTS(SELECT 1 FROM sys.extended_properties WHERE class=1 AND major_id=@id AND name=N'SAW_UtcDefaults_Original')
                    THROW 51210, 'UTC trigger rollback metadata already exists; review trigger state.', 1;
                SET @next=REPLACE(@old COLLATE Latin1_General_100_CI_AS,N'SYSDATETIME()',N'SYSUTCDATETIME()');
                IF @next=@old THROW 51210, 'Nonstandard clock expression in batch history trigger; review before migration.', 1;
                IF REPLACE(REPLACE(REPLACE(REPLACE(UPPER(LEFT(@next,200)),CHAR(13),N''),CHAR(10),N''),CHAR(9),N''),N' ',N'') LIKE N'CREATETRIGGER%'
                    SET @next=STUFF(@next,CHARINDEX(N'CREATE',UPPER(@next)),6,N'ALTER');
                DECLARE @saved nvarchar(3750)=@old;
                EXEC sys.sp_addextendedproperty @name=N'SAW_UtcDefaults_Original',@value=@saved,
                    @level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'PRODUCT_BATCH',
                    @level2type=N'TRIGGER',@level2name=N'TR_PRODUCT_BATCH_STATUS_HISTORY';
                EXEC sys.sp_executesql @next;
                IF @disabled=1 DISABLE TRIGGER dbo.TR_PRODUCT_BATCH_STATUS_HISTORY ON dbo.PRODUCT_BATCH;
            END
        END
        """;

    internal const string Down = """
        DECLARE @id int=OBJECT_ID(N'dbo.TR_PRODUCT_BATCH_STATUS_HISTORY',N'TR'), @old nvarchar(max), @expected nvarchar(max), @disabled bit;
        SELECT @old=CONVERT(nvarchar(3750),value) FROM sys.extended_properties
            WHERE class=1 AND major_id=@id AND name=N'SAW_UtcDefaults_Original';
        IF @old IS NOT NULL
        BEGIN
            SET @expected=REPLACE(@old COLLATE Latin1_General_100_CI_AS,N'SYSDATETIME()',N'SYSUTCDATETIME()');
            IF REPLACE(REPLACE(REPLACE(REPLACE(UPPER(LEFT(@expected,200)),CHAR(13),N''),CHAR(10),N''),CHAR(9),N''),N' ',N'') LIKE N'CREATETRIGGER%'
                SET @expected=STUFF(@expected,CHARINDEX(N'CREATE',UPPER(@expected)),6,N'ALTER');
            DECLARE @actual nvarchar(max)=OBJECT_DEFINITION(@id);
            -- SQL Server stores an ALTERed trigger with a CREATE header. Compare
            -- the name/table/body exactly, independent of that command verb.
            IF @actual IS NULL OR
                SUBSTRING(@actual,CHARINDEX(N'TRIGGER',UPPER(@actual)),LEN(@actual)) COLLATE Latin1_General_100_BIN2<>
                SUBSTRING(@expected,CHARINDEX(N'TRIGGER',UPPER(@expected)),LEN(@expected)) COLLATE Latin1_General_100_BIN2
                THROW 51211, 'Batch history trigger changed after UTC migration; refusing to overwrite it.', 1;
            SELECT @disabled=is_disabled FROM sys.triggers WHERE object_id=@id;
            IF REPLACE(REPLACE(REPLACE(REPLACE(UPPER(LEFT(@old,200)),CHAR(13),N''),CHAR(10),N''),CHAR(9),N''),N' ',N'') LIKE N'CREATETRIGGER%'
                SET @old=STUFF(@old,CHARINDEX(N'CREATE',UPPER(@old)),6,N'ALTER');
            EXEC sys.sp_executesql @old;
            IF @disabled=1 DISABLE TRIGGER dbo.TR_PRODUCT_BATCH_STATUS_HISTORY ON dbo.PRODUCT_BATCH;
            EXEC sys.sp_dropextendedproperty @name=N'SAW_UtcDefaults_Original',
                @level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'PRODUCT_BATCH',
                @level2type=N'TRIGGER',@level2name=N'TR_PRODUCT_BATCH_STATUS_HISTORY';
        END
        """;
}
