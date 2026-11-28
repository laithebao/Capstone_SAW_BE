using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace SAW.Infrastructure.Migrations;

public partial class NormalizeUtcTimestampDefaults : Migration
{
    private static readonly (string Table, string Column, bool CalendarDate)[] Columns =
    [
        ("WAREHOUSE_SETTING", "UpdatedAt", false),
        ("WAREHOUSE_LOCATION", "CreatedAt", false),
        ("SUPPLY_CONTRACT", "CreatedAt", false),
        ("SUPPLIER_GROWING_AREA", "JoinedAt", false),
        ("SUPPLIER_CROP_TYPE", "CreatedAt", false),
        ("SUPPLIER", "CreatedAt", false),
        ("STOCK_TRANSFER", "TransferredAt", false),
        ("STOCK_ADJUSTMENT", "CreatedAt", false),
        ("STANDARD_VERSION_REVIEW", "ReviewedAt", false),
        ("ROLE", "CreatedAt", false),
        ("REFRESH_TOKEN", "CreatedAt", false),
        ("QUALITY_IMAGE", "UploadedAt", false),
        ("QR_CODE", "GeneratedAt", false),
        ("QC_INSPECTION", "StartedAt", false),
        ("PURCHASE_ORDER", "CreatedAt", false),
        ("PRODUCT_BATCH", "CreatedAt", false),
        ("PICKING_HISTORY", "PickedAt", false),
        ("PASSWORD_RESET_TOKEN", "CreatedAt", false),
        ("ORDER_STATUS_HISTORY", "ChangedAt", false),
        ("NOTIFICATION", "CreatedAt", false),
        ("INVENTORY_RESERVATION", "ReservedAt", false),
        ("INVENTORY", "LastUpdatedAt", false),
        ("INSPECTION_STANDARD_VERSION", "CreatedAt", false),
        ("INSPECTION_STANDARD_SET", "CreatedAt", false),
        ("GOODS_RECEIPT", "ReceivedAt", true),
        ("GOODS_ISSUE", "IssuedAt", true),
        ("EMAIL_VERIFICATION_TOKEN", "CreatedAt", false),
        ("DISTRIBUTOR", "CreatedAt", false),
        ("CROP_TYPE", "CreatedAt", false),
        ("BATCH_STATUS_HISTORY", "ChangedAt", false),
        ("AUDIT_LOG", "CreatedAt", false),
        ("AI_RISK_FORECAST", "GeneratedAt", false),
        ("ACCOUNT_PERMISSION", "UpdatedAt", false),
        ("ACCOUNT", "CreatedAt", false)
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(UtcTimestampTriggerSql.Up);
        SetDefaults(migrationBuilder, upgrade: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(UtcTimestampTriggerSql.Down);
        SetDefaults(migrationBuilder, upgrade: false);
    }

    private static void SetDefaults(MigrationBuilder builder, bool upgrade)
    {
        foreach (var (table, column, calendarDate) in Columns)
        {
            var desired = calendarDate ? "CONVERT(date, DATEADD(hour, 7, SYSUTCDATETIME()))" : "SYSUTCDATETIME()";
            var next = upgrade ? desired : "SYSDATETIME()";
            var expected = upgrade ? "SYSDATETIME()" : desired;
            var canonical = expected.Replace("(", "").Replace(")", "").Replace(" ", "").ToUpperInvariant();
            // Only swap the default constraint. ALTER COLUMN would unnecessarily
            // change precision or conflict with existing baseline indexes on Down.
            builder.Sql($"""
                DECLARE @constraint sysname, @definition nvarchar(max);
                SELECT @constraint=d.name,@definition=d.definition FROM sys.default_constraints d
                JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
                WHERE d.parent_object_id=OBJECT_ID(N'[dbo].[{table}]') AND c.name=N'{column}';
                IF @definition IS NULL OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(@definition),N'[',N''),N']',N''),N'(',N''),N')',N''),N' ',N''),CHAR(13),N''),CHAR(10),N'')<>N'{canonical}'
                    THROW 51212, 'Unexpected default for {table}.{column}; review schema before migration.', 1;
                DECLARE @drop nvarchar(max)=N'ALTER TABLE [dbo].[{table}] DROP CONSTRAINT '+QUOTENAME(@constraint);
                EXEC sys.sp_executesql @drop;
                ALTER TABLE [dbo].[{table}] ADD DEFAULT ({next}) FOR [{column}];
                """);
        }
    }
}
