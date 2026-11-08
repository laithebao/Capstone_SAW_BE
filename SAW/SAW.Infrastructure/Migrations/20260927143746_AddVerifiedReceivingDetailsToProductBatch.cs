using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedReceivingDetailsToProductBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceivingNote",
                table: "PRODUCT_BATCH",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerifiedPackageCount",
                table: "PRODUCT_BATCH",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VerifiedPackageUnitWeightKg",
                table: "PRODUCT_BATCH",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedPackagingType",
                table: "PRODUCT_BATCH",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PRODUCT_BATCH_VerifiedPackageCount",
                table: "PRODUCT_BATCH",
                sql: "[VerifiedPackageCount] IS NULL OR [VerifiedPackageCount] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PRODUCT_BATCH_VerifiedPackageUnitWeightKg",
                table: "PRODUCT_BATCH",
                sql: "[VerifiedPackageUnitWeightKg] IS NULL OR [VerifiedPackageUnitWeightKg] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PRODUCT_BATCH_VerifiedPackageCount",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PRODUCT_BATCH_VerifiedPackageUnitWeightKg",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropColumn(
                name: "ReceivingNote",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropColumn(
                name: "VerifiedPackageCount",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropColumn(
                name: "VerifiedPackageUnitWeightKg",
                table: "PRODUCT_BATCH");

            migrationBuilder.DropColumn(
                name: "VerifiedPackagingType",
                table: "PRODUCT_BATCH");
        }
    }
}
