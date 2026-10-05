using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductBatchDailyCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PRODUCT_BATCH_DAILY_COUNTER",
                columns: table => new
                {
                    CodeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PRODUCT_BATCH_DAILY_COUNTER", x => x.CodeDate);
                    table.CheckConstraint("CK_PRODUCT_BATCH_DAILY_COUNTER_LastNumber", "[LastNumber] > 0");
                });
            // Preserve any previously issued numeric codes when installing the counter.
            migrationBuilder.Sql("""
                INSERT dbo.PRODUCT_BATCH_DAILY_COUNTER ([CodeDate], [LastNumber])
                SELECT [CodeDate], MAX([Number])
                FROM (
                    SELECT TRY_CONVERT(date, SUBSTRING([BatchCode], 4, 8), 112) AS [CodeDate],
                           TRY_CONVERT(int, SUBSTRING([BatchCode], 13, 38)) AS [Number]
                    FROM dbo.PRODUCT_BATCH
                    WHERE LEFT([BatchCode], 3) = 'LH-' AND SUBSTRING([BatchCode], 12, 1) = '-'
                      AND SUBSTRING([BatchCode], 13, 38) NOT LIKE '%[^0-9]%'
                ) AS existing
                WHERE [CodeDate] IS NOT NULL AND [Number] > 0
                GROUP BY [CodeDate];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PRODUCT_BATCH_DAILY_COUNTER");
        }
    }
}
