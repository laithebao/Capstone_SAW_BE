using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeEnvironmentLogWarehouseLocationNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ENVIRONMENT_LOG_LOCATION",
                table: "ENVIRONMENT_LOG");

            migrationBuilder.AlterColumn<int>(
                name: "WarehouseLocationID",
                table: "ENVIRONMENT_LOG",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddForeignKey(
                name: "FK_ENVIRONMENT_LOG_LOCATION",
                table: "ENVIRONMENT_LOG",
                column: "WarehouseLocationID",
                principalTable: "WAREHOUSE_LOCATION",
                principalColumn: "WarehouseLocationID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ENVIRONMENT_LOG_LOCATION",
                table: "ENVIRONMENT_LOG");

            migrationBuilder.AlterColumn<int>(
                name: "WarehouseLocationID",
                table: "ENVIRONMENT_LOG",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ENVIRONMENT_LOG_LOCATION",
                table: "ENVIRONMENT_LOG",
                column: "WarehouseLocationID",
                principalTable: "WAREHOUSE_LOCATION",
                principalColumn: "WarehouseLocationID",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
