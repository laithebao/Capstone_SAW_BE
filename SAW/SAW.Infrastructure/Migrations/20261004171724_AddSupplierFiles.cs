using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SUPPLIER_FILE",
                columns: table => new
                {
                    SupplierFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    ProductBatchId = table.Column<long>(type: "bigint", nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StorageName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SUPPLIER_FILE", x => x.SupplierFileId);
                    table.ForeignKey(
                        name: "FK_SUPPLIER_FILE_ACCOUNT_AccountId",
                        column: x => x.AccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SUPPLIER_FILE_PRODUCT_BATCH_ProductBatchId",
                        column: x => x.ProductBatchId,
                        principalTable: "PRODUCT_BATCH",
                        principalColumn: "ProductBatchID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SUPPLIER_FILE_SUPPLIER_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "SUPPLIER",
                        principalColumn: "SupplierID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLIER_FILE_AccountId",
                table: "SUPPLIER_FILE",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLIER_FILE_ProductBatchId",
                table: "SUPPLIER_FILE",
                column: "ProductBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLIER_FILE_SupplierId_ProductBatchId",
                table: "SUPPLIER_FILE",
                columns: new[] { "SupplierId", "ProductBatchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SUPPLIER_FILE");
        }
    }
}
