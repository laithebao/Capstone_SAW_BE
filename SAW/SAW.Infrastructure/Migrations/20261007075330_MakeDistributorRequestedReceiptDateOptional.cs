using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeDistributorRequestedReceiptDateOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateOnly>(
                name: "ExpectedDeliveryDate",
                table: "PURCHASE_ORDER",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM dbo.PURCHASE_ORDER WHERE ExpectedDeliveryDate IS NULL)
                    THROW 50320,'Cannot restore a mandatory receipt date while orders have no requested date.',1;
                """);
            migrationBuilder.AlterColumn<DateOnly>(
                name: "ExpectedDeliveryDate",
                table: "PURCHASE_ORDER",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }
    }
}
