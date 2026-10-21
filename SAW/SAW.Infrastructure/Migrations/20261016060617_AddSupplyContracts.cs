using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplyContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SUPPLY_CONTRACT",
                columns: table => new
                {
                    SupplyContractID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContractCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WarehouseManagerAccountID = table.Column<int>(type: "int", nullable: false),
                    SupplierID = table.Column<int>(type: "int", nullable: false),
                    CropTypeID = table.Column<int>(type: "int", nullable: false),
                    QualityGrade = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    DeliveriesPerWeek = table.Column<int>(type: "int", nullable: false),
                    UnitPriceVndPerKg = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Terms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ContractStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, defaultValue: "PENDING_SUPPLIER"),
                    SupplierRespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SupplierRejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AdminAccountID = table.Column<int>(type: "int", nullable: true),
                    AdminReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AdminRejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSDATETIME()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SUPPLY_CONTRACT", x => x.SupplyContractID);
                    table.CheckConstraint("CK_SUPPLY_CONTRACT_DateRange", "[EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_SUPPLY_CONTRACT_DeliveriesPerWeek", "[DeliveriesPerWeek] BETWEEN 1 AND 7");
                    table.CheckConstraint("CK_SUPPLY_CONTRACT_QualityGrade", "[QualityGrade] IN ('A','B','C','D','E')");
                    table.CheckConstraint("CK_SUPPLY_CONTRACT_Status", "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED')");
                    table.CheckConstraint("CK_SUPPLY_CONTRACT_UnitPrice", "[UnitPriceVndPerKg] > 0");
                    table.ForeignKey(
                        name: "FK_SUPPLY_CONTRACT_ADMIN",
                        column: x => x.AdminAccountID,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SUPPLY_CONTRACT_CROP_TYPE",
                        column: x => x.CropTypeID,
                        principalTable: "CROP_TYPE",
                        principalColumn: "CropTypeID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SUPPLY_CONTRACT_SUPPLIER",
                        column: x => x.SupplierID,
                        principalTable: "SUPPLIER",
                        principalColumn: "SupplierID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SUPPLY_CONTRACT_WAREHOUSE_MANAGER",
                        column: x => x.WarehouseManagerAccountID,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLY_CONTRACT_AdminAccountID",
                table: "SUPPLY_CONTRACT",
                column: "AdminAccountID");

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLY_CONTRACT_CropTypeID",
                table: "SUPPLY_CONTRACT",
                column: "CropTypeID");

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLY_CONTRACT_Supplier_Status",
                table: "SUPPLY_CONTRACT",
                columns: new[] { "SupplierID", "ContractStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLY_CONTRACT_Warehouse_Status",
                table: "SUPPLY_CONTRACT",
                columns: new[] { "WarehouseManagerAccountID", "ContractStatus" });

            migrationBuilder.CreateIndex(
                name: "UQ_SUPPLY_CONTRACT_Code",
                table: "SUPPLY_CONTRACT",
                column: "ContractCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SUPPLY_CONTRACT");
        }
    }
}
