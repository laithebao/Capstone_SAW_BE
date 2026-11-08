using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplyContractAttachmentFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddColumn<string>(
                name: "AttachmentContentType",
                table: "SUPPLY_CONTRACT",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentFileName",
                table: "SUPPLY_CONTRACT",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentStorageName",
                table: "SUPPLY_CONTRACT",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttachmentUploadedAt",
                table: "SUPPLY_CONTRACT",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT",
                sql: "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED','CANCELLATION_REQUESTED','PENDING_ADMIN_CANCELLATION','CANCELLATION_REJECTED','CANCELLED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "AttachmentContentType",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "AttachmentFileName",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "AttachmentStorageName",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "AttachmentUploadedAt",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT",
                sql: "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED','CANCELLATION_REQUESTED','CANCELLED')");
        }
    }
}
