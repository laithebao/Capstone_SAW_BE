using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockAdjustmentRequestScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InventorySnapshotJson",
                table: "STOCK_ADJUSTMENT",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "STOCK_ADJUSTMENT",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestKind",
                table: "STOCK_ADJUSTMENT",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "COUNT");

            migrationBuilder.AddColumn<string>(
                name: "RequestPayloadHash",
                table: "STOCK_ADJUSTMENT",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "STOCK_ADJUSTMENT_IMAGE",
                columns: table => new
                {
                    StockAdjustmentImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockAdjustmentId = table.Column<long>(type: "bigint", nullable: true),
                    UploadedByAccountId = table.Column<int>(type: "int", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssetId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_STOCK_ADJUSTMENT_IMAGE", x => x.StockAdjustmentImageId);
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_IMAGE_ACCOUNT_UploadedByAccountId",
                        column: x => x.UploadedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID");
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_IMAGE_STOCK_ADJUSTMENT_StockAdjustmentId",
                        column: x => x.StockAdjustmentId,
                        principalTable: "STOCK_ADJUSTMENT",
                        principalColumn: "StockAdjustmentID");
                });

            migrationBuilder.CreateTable(
                name: "STOCK_ADJUSTMENT_LINE",
                columns: table => new
                {
                    StockAdjustmentLineID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StockAdjustmentID = table.Column<long>(type: "bigint", nullable: false),
                    InventoryID = table.Column<long>(type: "bigint", nullable: false),
                    SystemQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ActualQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ReservedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InventoryUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LocationCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_STOCK_ADJUSTMENT_LINE", x => x.StockAdjustmentLineID);
                    table.CheckConstraint("CK_STOCK_ADJUSTMENT_LINE_Quantities", "[SystemQuantity]>=0 AND [ActualQuantity]>=0 AND [ReservedQuantity]>=0");
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_LINE_INVENTORY_InventoryID",
                        column: x => x.InventoryID,
                        principalTable: "INVENTORY",
                        principalColumn: "InventoryID");
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_LINE_STOCK_ADJUSTMENT_StockAdjustmentID",
                        column: x => x.StockAdjustmentID,
                        principalTable: "STOCK_ADJUSTMENT",
                        principalColumn: "StockAdjustmentID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_RequestId",
                table: "STOCK_ADJUSTMENT",
                column: "RequestId",
                unique: true,
                filter: "[RequestId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_STOCK_ADJUSTMENT_RequestKind",
                table: "STOCK_ADJUSTMENT",
                sql: "[RequestKind] IN ('COUNT','DISPOSAL')");

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_IMAGE_AssetId",
                table: "STOCK_ADJUSTMENT_IMAGE",
                column: "AssetId",
                unique: true);

            // New requests are immutable evidence. UC60 must process a disposal as one
            // whole-lot transaction, never as a legacy adjustment of only InventoryID.
            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER dbo.TR_STOCK_ADJUSTMENT_REQUEST_GUARD
                ON dbo.STOCK_ADJUSTMENT AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.StockAdjustmentID=d.StockAdjustmentID
                        WHERE d.RequestId IS NOT NULL AND (i.StockAdjustmentID IS NULL OR d.AdjustmentStatus<>'PENDING'
                            OR i.InventoryID<>d.InventoryID OR i.AdjustmentCode<>d.AdjustmentCode
                            OR i.RequestKind<>d.RequestKind OR i.SystemQuantity<>d.SystemQuantity OR i.ActualQuantity<>d.ActualQuantity
                            OR i.AdjustmentType<>d.AdjustmentType OR i.Reason<>d.Reason OR i.CreatedByAccountID<>d.CreatedByAccountID
                            OR i.CreatedAt<>d.CreatedAt OR ISNULL(CONVERT(nvarchar(36),i.RequestId),'')<>CONVERT(nvarchar(36),d.RequestId)
                            OR ISNULL(i.RequestPayloadHash,'')<>ISNULL(d.RequestPayloadHash,'')
                            OR ISNULL(i.InventorySnapshotJson,'')<>ISNULL(d.InventorySnapshotJson,'')))
                        THROW 50232,'Stock adjustment request evidence is immutable.',1;
                    IF EXISTS(SELECT 1 FROM inserted a JOIN deleted d ON a.StockAdjustmentID=d.StockAdjustmentID
                        JOIN INVENTORY anchor ON anchor.InventoryID=a.InventoryID
                        WHERE a.RequestId IS NOT NULL AND a.RequestKind='DISPOSAL' AND a.AdjustmentStatus='APPROVED'
                        AND (EXISTS(SELECT 1 FROM INVENTORY i WHERE i.ProductBatchID=anchor.ProductBatchID AND (i.QuantityOnHand<>0 OR i.ReservedQuantity<>0))
                            OR EXISTS(SELECT 1 FROM INVENTORY_RESERVATION r JOIN INVENTORY i ON i.InventoryID=r.InventoryID
                                WHERE i.ProductBatchID=anchor.ProductBatchID AND r.ReservationStatus IN ('RESERVED','PARTIALLY_PICKED','PICKED') AND r.ReleasedAt IS NULL)
                            OR EXISTS(SELECT 1 FROM PRODUCT_BATCH b WHERE b.ProductBatchID=anchor.ProductBatchID AND b.BatchStatus<>'DISPOSED')
                            OR EXISTS(SELECT 1 FROM INVENTORY i WHERE i.ProductBatchID=anchor.ProductBatchID AND NOT EXISTS(
                                SELECT 1 FROM STOCK_ADJUSTMENT_LINE l WHERE l.StockAdjustmentID=a.StockAdjustmentID AND l.InventoryID=i.InventoryID))))
                        THROW 50233,'UC60 must process all disposal inventory, reservations and batch status atomically.',1;
                END;
                """);
            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER dbo.TR_STOCK_ADJUSTMENT_LINE_IMMUTABLE
                ON dbo.STOCK_ADJUSTMENT_LINE AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted d JOIN STOCK_ADJUSTMENT a ON a.StockAdjustmentID=d.StockAdjustmentID WHERE a.RequestId IS NOT NULL)
                        THROW 50234,'Stock adjustment inventory snapshots are immutable.',1;
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_IMAGE_StockAdjustmentId",
                table: "STOCK_ADJUSTMENT_IMAGE",
                column: "StockAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_IMAGE_UploadedByAccountId",
                table: "STOCK_ADJUSTMENT_IMAGE",
                column: "UploadedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_LINE_InventoryID",
                table: "STOCK_ADJUSTMENT_LINE",
                column: "InventoryID");

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_LINE_StockAdjustmentID_InventoryID",
                table: "STOCK_ADJUSTMENT_LINE",
                columns: new[] { "StockAdjustmentID", "InventoryID" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_STOCK_ADJUSTMENT_REQUEST_GUARD; DROP TRIGGER IF EXISTS dbo.TR_STOCK_ADJUSTMENT_LINE_IMMUTABLE;");
            migrationBuilder.DropTable(
                name: "STOCK_ADJUSTMENT_IMAGE");

            migrationBuilder.DropTable(
                name: "STOCK_ADJUSTMENT_LINE");

            migrationBuilder.DropIndex(
                name: "IX_STOCK_ADJUSTMENT_RequestId",
                table: "STOCK_ADJUSTMENT");

            migrationBuilder.DropCheckConstraint(
                name: "CK_STOCK_ADJUSTMENT_RequestKind",
                table: "STOCK_ADJUSTMENT");

            migrationBuilder.DropColumn(
                name: "InventorySnapshotJson",
                table: "STOCK_ADJUSTMENT");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "STOCK_ADJUSTMENT");

            migrationBuilder.DropColumn(
                name: "RequestKind",
                table: "STOCK_ADJUSTMENT");

            migrationBuilder.DropColumn(
                name: "RequestPayloadHash",
                table: "STOCK_ADJUSTMENT");
        }
    }
}
