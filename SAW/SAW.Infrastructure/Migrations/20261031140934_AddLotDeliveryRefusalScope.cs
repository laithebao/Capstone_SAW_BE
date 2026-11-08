using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLotDeliveryRefusalScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS(SELECT 1 FROM dbo.ORDER_DELIVERY_REFUSAL r
                    WHERE NOT EXISTS(SELECT 1 FROM dbo.ORDER_DETAIL l WHERE l.PurchaseOrderID=r.PurchaseOrderId))
                    THROW 50343,'An existing refusal has no order lines; cannot assign its scope safely.',1;
                DECLARE @before nvarchar(max)=(SELECT definition FROM sys.check_constraints
                    WHERE parent_object_id=OBJECT_ID(N'dbo.PURCHASE_ORDER') AND name=N'CK_PURCHASE_ORDER_Status');
                IF @before IS NOT NULL
                BEGIN
                    IF LEN(@before)>3900 THROW 50343,'Order status constraint is too long to preserve safely.',1;
                    DECLARE @saved nvarchar(4000)=@before;
                    EXEC sys.sp_addextendedproperty @name=N'SAW_LotRefusal_PreviousStatusCheck',@value=@saved,
                        @level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'PURCHASE_ORDER';
                    ALTER TABLE dbo.PURCHASE_ORDER DROP CONSTRAINT CK_PURCHASE_ORDER_Status;
                    DECLARE @sql nvarchar(max)=N'ALTER TABLE dbo.PURCHASE_ORDER WITH CHECK ADD CONSTRAINT CK_PURCHASE_ORDER_Status CHECK (('+@before+N') OR [OrderStatus]=N''PARTIALLY_REFUSED'')';
                    EXEC sys.sp_executesql @sql;
                END;
                """);
            migrationBuilder.DropIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_PurchaseOrderId",
                table: "ORDER_DELIVERY_REFUSAL");

            migrationBuilder.CreateTable(
                name: "ORDER_DELIVERY_REFUSAL_LOT",
                columns: table => new
                {
                    OrderDetailId = table.Column<long>(type: "bigint", nullable: false),
                    OrderDeliveryRefusalId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ORDER_DELIVERY_REFUSAL_LOT", x => x.OrderDetailId);
                    table.ForeignKey(
                        name: "FK_ORDER_DELIVERY_REFUSAL_LOT_ORDER_DELIVERY_REFUSAL_OrderDeliveryRefusalId",
                        column: x => x.OrderDeliveryRefusalId,
                        principalTable: "ORDER_DELIVERY_REFUSAL",
                        principalColumn: "OrderDeliveryRefusalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ORDER_DELIVERY_REFUSAL_LOT_ORDER_DETAIL_OrderDetailId",
                        column: x => x.OrderDetailId,
                        principalTable: "ORDER_DETAIL",
                        principalColumn: "OrderDetailID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_PurchaseOrderId",
                table: "ORDER_DELIVERY_REFUSAL",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_LOT_OrderDeliveryRefusalId",
                table: "ORDER_DELIVERY_REFUSAL_LOT",
                column: "OrderDeliveryRefusalId");
            // Previous reports explicitly refused the whole order. Preserve that exact scope and every photo.
            migrationBuilder.Sql("""
                INSERT INTO dbo.ORDER_DELIVERY_REFUSAL_LOT(OrderDetailId,OrderDeliveryRefusalId)
                SELECT l.OrderDetailID,r.OrderDeliveryRefusalId FROM dbo.ORDER_DELIVERY_REFUSAL r
                JOIN dbo.ORDER_DETAIL l ON l.PurchaseOrderID=r.PurchaseOrderId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS(SELECT 1 FROM dbo.ORDER_DELIVERY_REFUSAL_LOT)
                    OR EXISTS(SELECT 1 FROM dbo.PURCHASE_ORDER WHERE OrderStatus=N'PARTIALLY_REFUSED')
                    THROW 50344,'Cannot remove recorded lot refusals or convert partial refusals to whole-order refusals.',1;
                DECLARE @before nvarchar(max)=(SELECT CONVERT(nvarchar(4000),value) FROM sys.extended_properties
                    WHERE major_id=OBJECT_ID(N'dbo.PURCHASE_ORDER') AND minor_id=0 AND name=N'SAW_LotRefusal_PreviousStatusCheck');
                IF @before IS NOT NULL
                BEGIN
                    ALTER TABLE dbo.PURCHASE_ORDER DROP CONSTRAINT CK_PURCHASE_ORDER_Status;
                    DECLARE @sql nvarchar(max)=N'ALTER TABLE dbo.PURCHASE_ORDER WITH CHECK ADD CONSTRAINT CK_PURCHASE_ORDER_Status CHECK '+@before;
                    EXEC sys.sp_executesql @sql;
                    EXEC sys.sp_dropextendedproperty @name=N'SAW_LotRefusal_PreviousStatusCheck',
                        @level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'PURCHASE_ORDER';
                END;
                """);
            migrationBuilder.DropTable(
                name: "ORDER_DELIVERY_REFUSAL_LOT");

            migrationBuilder.DropIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_PurchaseOrderId",
                table: "ORDER_DELIVERY_REFUSAL");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_DELIVERY_REFUSAL_PurchaseOrderId",
                table: "ORDER_DELIVERY_REFUSAL",
                column: "PurchaseOrderId",
                unique: true);
        }
    }
}
