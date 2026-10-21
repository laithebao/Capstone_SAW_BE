using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceDeliveryFrequencyWithPeriodicQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_DeliveriesPerWeek",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryPeriod",
                table: "SUPPLY_CONTRACT",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryQuantityKg",
                table: "SUPPLY_CONTRACT",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [SUPPLY_CONTRACT]
                SET [DeliveryQuantityKg] = CAST([DeliveriesPerWeek] AS decimal(18,2)),
                    [DeliveryPeriod] = 'WEEK';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "DeliveryPeriod",
                table: "SUPPLY_CONTRACT",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "DeliveryQuantityKg",
                table: "SUPPLY_CONTRACT",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "DeliveriesPerWeek",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_DeliveryPeriod",
                table: "SUPPLY_CONTRACT",
                sql: "[DeliveryPeriod] IN ('WEEK','MONTH')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_DeliveryQuantity",
                table: "SUPPLY_CONTRACT",
                sql: "[DeliveryQuantityKg] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_DeliveryPeriod",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_DeliveryQuantity",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddColumn<int>(
                name: "DeliveriesPerWeek",
                table: "SUPPLY_CONTRACT",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("UPDATE [SUPPLY_CONTRACT] SET [DeliveriesPerWeek] = 1;");

            migrationBuilder.AlterColumn<int>(
                name: "DeliveriesPerWeek",
                table: "SUPPLY_CONTRACT",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "DeliveryPeriod",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.DropColumn(
                name: "DeliveryQuantityKg",
                table: "SUPPLY_CONTRACT");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SUPPLY_CONTRACT_DeliveriesPerWeek",
                table: "SUPPLY_CONTRACT",
                sql: "[DeliveriesPerWeek] BETWEEN 1 AND 7");
        }
    }
}
