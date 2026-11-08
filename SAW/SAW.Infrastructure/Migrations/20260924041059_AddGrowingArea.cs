using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGrowingArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrowingArea",
                table: "SUPPLIER");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "PRODUCT_BATCH");

            migrationBuilder.AddColumn<int>(
                name: "GrowingAreaID",
                table: "PRODUCT_BATCH",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GROWING_AREA",
                columns: table => new
                {
                    GrowingAreaId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AreaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Region = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Province = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    District = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Ward = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GROWING_AREA", x => x.GrowingAreaId);
                });

            migrationBuilder.CreateTable(
                name: "SUPPLIER_GROWING_AREA",
                columns: table => new
                {
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    GrowingAreaId = table.Column<int>(type: "int", nullable: false),
                    AreaInHectares = table.Column<decimal>(type: "DECIMAL(18,2)", nullable: true),
                    SupplierSpecificNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    JoinedAt = table.Column<DateTime>(type: "DATETIME2(0)", nullable: false, defaultValueSql: "SYSDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SUPPLIER_GROWING_AREA", x => new { x.SupplierId, x.GrowingAreaId });
                    table.ForeignKey(
                        name: "FK_SUPPLIER_GROWING_AREA_AREA",
                        column: x => x.GrowingAreaId,
                        principalTable: "GROWING_AREA",
                        principalColumn: "GrowingAreaId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SUPPLIER_GROWING_AREA_SUPPLIER",
                        column: x => x.SupplierId,
                        principalTable: "SUPPLIER",
                        principalColumn: "SupplierID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PRODUCT_BATCH_GrowingAreaID",
                table: "PRODUCT_BATCH",
                column: "GrowingAreaID");

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLIER_GROWING_AREA_GrowingAreaId",
                table: "SUPPLIER_GROWING_AREA",
                column: "GrowingAreaId");

            migrationBuilder.AddForeignKey(
                name: "FK_PRODUCT_BATCH_GROWING_AREA",
                table: "PRODUCT_BATCH",
                column: "GrowingAreaID",
                principalTable: "GROWING_AREA",
                principalColumn: "GrowingAreaId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PRODUCT_BATCH_GROWING_AREA",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropTable(
                name: "SUPPLIER_GROWING_AREA");

            migrationBuilder.DropTable(
                name: "GROWING_AREA");

            migrationBuilder.DropIndex(
                name: "IX_PRODUCT_BATCH_GrowingAreaID",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropColumn(
                name: "GrowingAreaID",
                table: "PRODUCT_BATCH");

            migrationBuilder.AddColumn<string>(
                name: "GrowingArea",
                table: "SUPPLIER",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "PRODUCT_BATCH",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");
        }
    }
}
