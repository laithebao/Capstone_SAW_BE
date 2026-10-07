using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributorWholeLotOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedAt",
                table: "PURCHASE_ORDER",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReceivedByAccountID",
                table: "PURCHASE_ORDER",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BATCH_SALE_OFFER",
                columns: table => new
                {
                    ProductBatchID = table.Column<long>(type: "bigint", nullable: false),
                    WholeLotPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BATCH_SALE_OFFER", x => x.ProductBatchID);
                    table.CheckConstraint("CK_BATCH_SALE_OFFER_Price", "[WholeLotPrice] > 0");
                    table.ForeignKey(
                        name: "FK_BATCH_SALE_OFFER_PRODUCT_BATCH_ProductBatchID",
                        column: x => x.ProductBatchID,
                        principalTable: "PRODUCT_BATCH",
                        principalColumn: "ProductBatchID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PURCHASE_ORDER_ReceivedByAccountID",
                table: "PURCHASE_ORDER",
                column: "ReceivedByAccountID");

            migrationBuilder.AddForeignKey(
                name: "FK_PURCHASE_ORDER_ACCOUNT_ReceivedByAccountID",
                table: "PURCHASE_ORDER",
                column: "ReceivedByAccountID",
                principalTable: "ACCOUNT",
                principalColumn: "AccountID",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PURCHASE_ORDER_ACCOUNT_ReceivedByAccountID",
                table: "PURCHASE_ORDER");

            migrationBuilder.DropTable(
                name: "BATCH_SALE_OFFER");

            migrationBuilder.DropIndex(
                name: "IX_PURCHASE_ORDER_ReceivedByAccountID",
                table: "PURCHASE_ORDER");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "PURCHASE_ORDER");

            migrationBuilder.DropColumn(
                name: "ReceivedByAccountID",
                table: "PURCHASE_ORDER");
        }
    }
}
