using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations;

[Migration("20261022073000_AllowPendingAdminContractCancellation")]
public partial class AllowPendingAdminContractCancellation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_SUPPLY_CONTRACT_Status",
            table: "SUPPLY_CONTRACT");

        migrationBuilder.AddCheckConstraint(
            name: "CK_SUPPLY_CONTRACT_Status",
            table: "SUPPLY_CONTRACT",
            sql: "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED','CANCELLATION_REQUESTED','PENDING_ADMIN_CANCELLATION','CANCELLED')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_SUPPLY_CONTRACT_Status",
            table: "SUPPLY_CONTRACT");

        migrationBuilder.AddCheckConstraint(
            name: "CK_SUPPLY_CONTRACT_Status",
            table: "SUPPLY_CONTRACT",
            sql: "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED','CANCELLATION_REQUESTED','CANCELLED')");
    }
}
