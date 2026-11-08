using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkProductBatchToSupplyContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SupplyContractID",
                table: "PRODUCT_BATCH",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PRODUCT_BATCH_SupplyContractID",
                table: "PRODUCT_BATCH",
                column: "SupplyContractID");

            migrationBuilder.AddForeignKey(
                name: "FK_PRODUCT_BATCH_SUPPLY_CONTRACT",
                table: "PRODUCT_BATCH",
                column: "SupplyContractID",
                principalTable: "SUPPLY_CONTRACT",
                principalColumn: "SupplyContractID",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PRODUCT_BATCH_SUPPLY_CONTRACT",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropIndex(
                name: "IX_PRODUCT_BATCH_SupplyContractID",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropColumn(
                name: "SupplyContractID",
                table: "PRODUCT_BATCH");
        }
    }
}
