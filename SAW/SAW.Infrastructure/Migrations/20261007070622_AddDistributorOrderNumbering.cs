using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributorOrderNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exported warehouses may use IX_PURCHASE_ORDER_Distributor_Status instead of EF's conventional index.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PURCHASE_ORDER') AND name=N'IX_PURCHASE_ORDER_DistributorID')
                    DROP INDEX [IX_PURCHASE_ORDER_DistributorID] ON dbo.PURCHASE_ORDER;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "PURCHASE_ORDER",
                type: "uniqueidentifier",
                nullable: true);

            // Keep old public order codes intact and preserve retry identity for orders created before this migration.
            migrationBuilder.Sql("""
                UPDATE po SET RequestId=TRY_CONVERT(uniqueidentifier,
                    STUFF(STUFF(STUFF(STUFF(RIGHT(po.OrderCode,32),21,0,'-'),17,0,'-'),13,0,'-'),9,0,'-'))
                FROM dbo.PURCHASE_ORDER po JOIN dbo.DISTRIBUTOR d ON d.DistributorID=po.DistributorID
                WHERE LEFT(po.OrderCode,LEN(CONCAT('PO-',d.AccountID,'-')))=CONCAT('PO-',d.AccountID,'-')
                  AND LEN(po.OrderCode)=LEN(CONCAT('PO-',d.AccountID,'-'))+32
                  AND RIGHT(po.OrderCode,32) NOT LIKE '%[^0-9a-fA-F]%';
                """);

            migrationBuilder.CreateTable(
                name: "PURCHASE_ORDER_DAILY_COUNTER",
                columns: table => new
                {
                    CodeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PURCHASE_ORDER_DAILY_COUNTER", x => x.CodeDate);
                    table.CheckConstraint("CK_PURCHASE_ORDER_DAILY_COUNTER_Number", "[LastNumber] > 0");
                });

            migrationBuilder.Sql("""
                INSERT dbo.PURCHASE_ORDER_DAILY_COUNTER (CodeDate,LastNumber)
                SELECT CodeDate,MAX(Number)
                FROM (
                    SELECT TRY_CONVERT(date,SUBSTRING(OrderCode,4,8),112) AS CodeDate,
                           TRY_CONVERT(int,SUBSTRING(OrderCode,13,38)) AS Number
                    FROM dbo.PURCHASE_ORDER
                    WHERE LEFT(OrderCode,3)='PO-' AND SUBSTRING(OrderCode,12,1)='-'
                      AND SUBSTRING(OrderCode,13,38) NOT LIKE '%[^0-9]%'
                ) codes
                WHERE CodeDate IS NOT NULL AND Number>0
                GROUP BY CodeDate;
                """);

            migrationBuilder.CreateIndex(
                name: "UQ_PURCHASE_ORDER_Distributor_Request",
                table: "PURCHASE_ORDER",
                columns: new[] { "DistributorID", "RequestId" },
                unique: true,
                filter: "[RequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PURCHASE_ORDER_DAILY_COUNTER");

            migrationBuilder.DropIndex(
                name: "UQ_PURCHASE_ORDER_Distributor_Request",
                table: "PURCHASE_ORDER");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "PURCHASE_ORDER");

            migrationBuilder.CreateIndex(
                name: "IX_PURCHASE_ORDER_DistributorID",
                table: "PURCHASE_ORDER",
                column: "DistributorID");
        }
    }
}
