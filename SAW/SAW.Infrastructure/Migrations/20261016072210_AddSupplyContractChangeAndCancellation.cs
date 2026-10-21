using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplyContractChangeAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "SUPPLY_CONTRACT",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestReason",
                table: "SUPPLY_CONTRACT",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationRequestedAt",
                table: "SUPPLY_CONTRACT",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "SUPPLY_CONTRACT",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "SUPPLY_CONTRACT",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT",
                sql: "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED','CANCELLATION_REQUESTED','CANCELLED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "CancellationRequestReason",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAt",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_Status",
                table: "SUPPLY_CONTRACT",
                sql: "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED')");
        }
    }
}
