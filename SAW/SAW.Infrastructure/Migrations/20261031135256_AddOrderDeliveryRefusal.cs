using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderDeliveryRefusal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @before nvarchar(max) = (SELECT definition FROM sys.check_constraints
                    WHERE parent_object_id=OBJECT_ID(N'dbo.PURCHASE_ORDER') AND name=N'CK_PURCHASE_ORDER_Status');
                IF @before IS NOT NULL
                BEGIN
                    IF LEN(@before)>3900 THROW 50340,'Order status constraint is too long to preserve safely.',1;
                    DECLARE @saved nvarchar(4000)=@before;
                    EXEC sys.sp_addextendedproperty @name=N'SAW_DeliveryRefusal_PreviousStatusCheck',@value=@saved,
                        @level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'PURCHASE_ORDER';
                    ALTER TABLE dbo.PURCHASE_ORDER DROP CONSTRAINT CK_PURCHASE_ORDER_Status;
                    DECLARE @sql nvarchar(max)=N'ALTER TABLE dbo.PURCHASE_ORDER WITH CHECK ADD CONSTRAINT CK_PURCHASE_ORDER_Status CHECK (('+@before+N') OR [OrderStatus]=N''DELIVERY_REFUSED'')';
                    EXEC sys.sp_executesql @sql;
                END;
                """);
            migrationBuilder.CreateTable(
                name: "ORDER_DELIVERY_REFUSAL",
                columns: table => new
                {
                    OrderDeliveryRefusalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByAccountId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ORDER_DELIVERY_REFUSAL", x => x.OrderDeliveryRefusalId);
                    table.ForeignKey(
                        name: "FK_ORDER_DELIVERY_REFUSAL_ACCOUNT_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ORDER_DELIVERY_REFUSAL_PURCHASE_ORDER_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PURCHASE_ORDER",
                        principalColumn: "PurchaseOrderID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ORDER_DELIVERY_REFUSAL_IMAGE",
                columns: table => new
                {
                    OrderDeliveryRefusalImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderDeliveryRefusalId = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    StorageName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ORDER_DELIVERY_REFUSAL_IMAGE", x => x.OrderDeliveryRefusalImageId);
                    table.ForeignKey(
                        name: "FK_ORDER_DELIVERY_REFUSAL_IMAGE_ORDER_DELIVERY_REFUSAL_OrderDeliveryRefusalId",
                        column: x => x.OrderDeliveryRefusalId,
                        principalTable: "ORDER_DELIVERY_REFUSAL",
                        principalColumn: "OrderDeliveryRefusalId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_CreatedByAccountId",
                table: "ORDER_DELIVERY_REFUSAL",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_PurchaseOrderId",
                table: "ORDER_DELIVERY_REFUSAL",
                column: "PurchaseOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_RequestId",
                table: "ORDER_DELIVERY_REFUSAL",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_IMAGE_OrderDeliveryRefusalId",
                table: "ORDER_DELIVERY_REFUSAL_IMAGE",
                column: "OrderDeliveryRefusalId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_IMAGE_StorageName",
                table: "ORDER_DELIVERY_REFUSAL_IMAGE",
                column: "StorageName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS(SELECT 1 FROM dbo.ORDER_DELIVERY_REFUSAL)
                    OR EXISTS(SELECT 1 FROM dbo.PURCHASE_ORDER WHERE OrderStatus=N'DELIVERY_REFUSED')
                    THROW 50341,'Cannot remove delivery refusal evidence or downgrade orders that have been refused.',1;
                DECLARE @before nvarchar(max)=(SELECT CONVERT(nvarchar(4000),value) FROM sys.extended_properties
                    WHERE major_id=OBJECT_ID(N'dbo.PURCHASE_ORDER') AND minor_id=0 AND name=N'SAW_DeliveryRefusal_PreviousStatusCheck');
                IF @before IS NOT NULL
                BEGIN
                    ALTER TABLE dbo.PURCHASE_ORDER DROP CONSTRAINT CK_PURCHASE_ORDER_Status;
                    DECLARE @sql nvarchar(max)=N'ALTER TABLE dbo.PURCHASE_ORDER WITH CHECK ADD CONSTRAINT CK_PURCHASE_ORDER_Status CHECK '+@before;
                    EXEC sys.sp_executesql @sql;
                    EXEC sys.sp_dropextendedproperty @name=N'SAW_DeliveryRefusal_PreviousStatusCheck',
                        @level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'PURCHASE_ORDER';
                END;
                """);
            migrationBuilder.DropTable(
                name: "ORDER_DELIVERY_REFUSAL_IMAGE");

            migrationBuilder.DropTable(
                name: "ORDER_DELIVERY_REFUSAL");
        }
    }
}
