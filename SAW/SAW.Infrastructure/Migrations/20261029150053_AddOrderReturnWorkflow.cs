using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderReturnWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.CK_PURCHASE_ORDER_Status', N'C') IS NOT NULL
                    ALTER TABLE dbo.PURCHASE_ORDER DROP CONSTRAINT CK_PURCHASE_ORDER_Status;
                ALTER TABLE dbo.PURCHASE_ORDER ADD CONSTRAINT CK_PURCHASE_ORDER_Status CHECK
                (OrderStatus IN ('PENDING','APPROVED','RESERVED','PARTIALLY_PICKED','PICKED','PREPARED','PARTIALLY_ISSUED','ISSUED','DISPATCHED','DELIVERED','REJECTED','CANCELLED','RETURNED'));
                """);
            migrationBuilder.CreateTable(
                name: "ORDER_RETURN",
                columns: table => new
                {
                    OrderReturnId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ReviewNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReceiptNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RequestedByAccountId = table.Column<int>(type: "int", nullable: false),
                    ReviewedByAccountId = table.Column<int>(type: "int", nullable: true),
                    ReceivedByAccountId = table.Column<int>(type: "int", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ORDER_RETURN", x => x.OrderReturnId);
                    table.CheckConstraint("CK_ORDER_RETURN_Status", "[Status] IN ('REQUESTED','APPROVED','REJECTED','RECEIVED')");
                    table.ForeignKey(
                        name: "FK_ORDER_RETURN_ACCOUNT_ReceivedByAccountId",
                        column: x => x.ReceivedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ORDER_RETURN_ACCOUNT_RequestedByAccountId",
                        column: x => x.RequestedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ORDER_RETURN_ACCOUNT_ReviewedByAccountId",
                        column: x => x.ReviewedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ORDER_RETURN_PURCHASE_ORDER_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PURCHASE_ORDER",
                        principalColumn: "PurchaseOrderID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_RETURN_PurchaseOrderId",
                table: "ORDER_RETURN",
                column: "PurchaseOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_RETURN_ReceivedByAccountId",
                table: "ORDER_RETURN",
                column: "ReceivedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_RETURN_RequestedByAccountId",
                table: "ORDER_RETURN",
                column: "RequestedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_RETURN_ReviewedByAccountId",
                table: "ORDER_RETURN",
                column: "ReviewedByAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM dbo.PURCHASE_ORDER WHERE OrderStatus = 'RETURNED')
                    THROW 50330, 'Cannot remove return workflow while returned orders exist.', 1;
                ALTER TABLE dbo.PURCHASE_ORDER DROP CONSTRAINT CK_PURCHASE_ORDER_Status;
                ALTER TABLE dbo.PURCHASE_ORDER ADD CONSTRAINT CK_PURCHASE_ORDER_Status CHECK
                (OrderStatus IN ('PENDING','APPROVED','RESERVED','PARTIALLY_PICKED','PICKED','PREPARED','PARTIALLY_ISSUED','ISSUED','DISPATCHED','DELIVERED','REJECTED','CANCELLED'));
                """);
            migrationBuilder.DropTable(
                name: "ORDER_RETURN");
        }
    }
}
