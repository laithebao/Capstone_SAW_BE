using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueActiveBatchQrCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UQ_QR_CODE_ActiveBatch",
                table: "QR_CODE",
                column: "ProductBatchID",
                unique: true,
                filter: "[IsActive] = 1 AND [PackageCode] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_QR_CODE_ActiveBatch",
                table: "QR_CODE");
        }
    }
}
