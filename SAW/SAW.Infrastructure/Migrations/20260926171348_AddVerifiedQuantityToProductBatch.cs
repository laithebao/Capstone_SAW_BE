using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedQuantityToProductBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "VerifiedQuantity",
                table: "PRODUCT_BATCH",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VerifiedWeightInKg",
                table: "PRODUCT_BATCH",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerifiedQuantity",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropColumn(
                name: "VerifiedWeightInKg",
                table: "PRODUCT_BATCH");
        }
    }
}
