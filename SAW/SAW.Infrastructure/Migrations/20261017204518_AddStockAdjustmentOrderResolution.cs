using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockAdjustmentOrderResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "DeletePending", table: "STOCK_ADJUSTMENT_IMAGE", type: "bit", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<decimal>(
                name: "ApprovedRemainderWeightKg",
                table: "BATCH_SALE_OFFER",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RepricedForAdjustmentId",
                table: "BATCH_SALE_OFFER",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceStockAdjustmentId",
                table: "BATCH_SALE_OFFER",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GOODS_ISSUE_INVALIDATION",
                columns: table => new
                {
                    GoodsIssueId = table.Column<long>(type: "bigint", nullable: false),
                    StockAdjustmentId = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    InvalidatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GOODS_ISSUE_INVALIDATION", x => x.GoodsIssueId);
                    table.ForeignKey(
                        name: "FK_GOODS_ISSUE_INVALIDATION_ACCOUNT_AccountId",
                        column: x => x.AccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GOODS_ISSUE_INVALIDATION_GOODS_ISSUE_GoodsIssueId",
                        column: x => x.GoodsIssueId,
                        principalTable: "GOODS_ISSUE",
                        principalColumn: "GoodsIssueID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GOODS_ISSUE_INVALIDATION_STOCK_ADJUSTMENT_StockAdjustmentId",
                        column: x => x.StockAdjustmentId,
                        principalTable: "STOCK_ADJUSTMENT",
                        principalColumn: "StockAdjustmentID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "STOCK_ADJUSTMENT_RESOLUTION",
                columns: table => new
                {
                    StockAdjustmentId = table.Column<long>(type: "bigint", nullable: false),
                    PurchaseOrderId = table.Column<long>(type: "bigint", nullable: true),
                    OrderDetailId = table.Column<long>(type: "bigint", nullable: true),
                    ReplacesStockAdjustmentId = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CoordinatingStockAdjustmentId = table.Column<long>(type: "bigint", nullable: true),
                    ResolvedBatchWeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    ExpectedInventorySnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpectedOrderSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolutionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_STOCK_ADJUSTMENT_RESOLUTION", x => x.StockAdjustmentId);
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_RESOLUTION_ORDER_DETAIL_OrderDetailId",
                        column: x => x.OrderDetailId,
                        principalTable: "ORDER_DETAIL",
                        principalColumn: "OrderDetailID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_RESOLUTION_PURCHASE_ORDER_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PURCHASE_ORDER",
                        principalColumn: "PurchaseOrderID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_RESOLUTION_STOCK_ADJUSTMENT_ReplacesStockAdjustmentId",
                        column: x => x.ReplacesStockAdjustmentId,
                        principalTable: "STOCK_ADJUSTMENT",
                        principalColumn: "StockAdjustmentID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_RESOLUTION_STOCK_ADJUSTMENT_StockAdjustmentId",
                        column: x => x.StockAdjustmentId,
                        principalTable: "STOCK_ADJUSTMENT",
                        principalColumn: "StockAdjustmentID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "STOCK_ADJUSTMENT_PROPOSAL",
                columns: table => new
                {
                    StockAdjustmentProposalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StockAdjustmentId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ProposalStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OriginalWeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ProposedWeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    OriginalWholeLotPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ProposedWholeLotPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OriginalOrderTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ProposedOrderTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ScopeSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomerMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedByAccountId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RespondedByAccountId = table.Column<int>(type: "int", nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResponseReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WithdrawalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_STOCK_ADJUSTMENT_PROPOSAL", x => x.StockAdjustmentProposalId);
                    table.CheckConstraint("CK_ADJUSTMENT_PROPOSAL_Amounts", "[ProposedWeightKg] > 0 AND [ProposedWholeLotPrice] > 0");
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_PROPOSAL_ACCOUNT_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_PROPOSAL_ACCOUNT_RespondedByAccountId",
                        column: x => x.RespondedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_STOCK_ADJUSTMENT_PROPOSAL_STOCK_ADJUSTMENT_RESOLUTION_StockAdjustmentId",
                        column: x => x.StockAdjustmentId,
                        principalTable: "STOCK_ADJUSTMENT_RESOLUTION",
                        principalColumn: "StockAdjustmentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ORDER_AMENDMENT",
                columns: table => new
                {
                    OrderAmendmentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StockAdjustmentProposalId = table.Column<long>(type: "bigint", nullable: false),
                    OrderDetailId = table.Column<long>(type: "bigint", nullable: false),
                    PreviousWeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    EffectiveWeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PreviousWholeLotPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EffectiveWholeLotPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousOrderTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EffectiveOrderTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ApprovedByAccountId = table.Column<int>(type: "int", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ORDER_AMENDMENT", x => x.OrderAmendmentId);
                    table.ForeignKey(
                        name: "FK_ORDER_AMENDMENT_ACCOUNT_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ORDER_AMENDMENT_ORDER_DETAIL_OrderDetailId",
                        column: x => x.OrderDetailId,
                        principalTable: "ORDER_DETAIL",
                        principalColumn: "OrderDetailID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ORDER_AMENDMENT_STOCK_ADJUSTMENT_PROPOSAL_StockAdjustmentProposalId",
                        column: x => x.StockAdjustmentProposalId,
                        principalTable: "STOCK_ADJUSTMENT_PROPOSAL",
                        principalColumn: "StockAdjustmentProposalId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BATCH_SALE_OFFER_RepricedForAdjustmentId",
                table: "BATCH_SALE_OFFER",
                column: "RepricedForAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_BATCH_SALE_OFFER_SourceStockAdjustmentId",
                table: "BATCH_SALE_OFFER",
                column: "SourceStockAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_GOODS_ISSUE_INVALIDATION_AccountId",
                table: "GOODS_ISSUE_INVALIDATION",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GOODS_ISSUE_INVALIDATION_StockAdjustmentId",
                table: "GOODS_ISSUE_INVALIDATION",
                column: "StockAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_AMENDMENT_ApprovedByAccountId",
                table: "ORDER_AMENDMENT",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_AMENDMENT_OrderDetailId",
                table: "ORDER_AMENDMENT",
                column: "OrderDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_ORDER_AMENDMENT_StockAdjustmentProposalId",
                table: "ORDER_AMENDMENT",
                column: "StockAdjustmentProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_PROPOSAL_CreatedByAccountId",
                table: "STOCK_ADJUSTMENT_PROPOSAL",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_PROPOSAL_RespondedByAccountId",
                table: "STOCK_ADJUSTMENT_PROPOSAL",
                column: "RespondedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_PROPOSAL_StockAdjustmentId_Revision",
                table: "STOCK_ADJUSTMENT_PROPOSAL",
                columns: new[] { "StockAdjustmentId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_RESOLUTION_OrderDetailId",
                table: "STOCK_ADJUSTMENT_RESOLUTION",
                column: "OrderDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_RESOLUTION_PurchaseOrderId_State",
                table: "STOCK_ADJUSTMENT_RESOLUTION",
                columns: new[] { "PurchaseOrderId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_STOCK_ADJUSTMENT_RESOLUTION_ReplacesStockAdjustmentId",
                table: "STOCK_ADJUSTMENT_RESOLUTION",
                column: "ReplacesStockAdjustmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_BATCH_SALE_OFFER_STOCK_ADJUSTMENT_RepricedForAdjustmentId",
                table: "BATCH_SALE_OFFER",
                column: "RepricedForAdjustmentId",
                principalTable: "STOCK_ADJUSTMENT",
                principalColumn: "StockAdjustmentID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BATCH_SALE_OFFER_STOCK_ADJUSTMENT_SourceStockAdjustmentId",
                table: "BATCH_SALE_OFFER",
                column: "SourceStockAdjustmentId",
                principalTable: "STOCK_ADJUSTMENT",
                principalColumn: "StockAdjustmentID",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.CreateIndex(name: "IX_STOCK_ADJUSTMENT_RESOLUTION_CoordinatingStockAdjustmentId", table: "STOCK_ADJUSTMENT_RESOLUTION", column: "CoordinatingStockAdjustmentId");
            migrationBuilder.AddForeignKey(name: "FK_STOCK_ADJUSTMENT_RESOLUTION_STOCK_ADJUSTMENT_CoordinatingStockAdjustmentId", table: "STOCK_ADJUSTMENT_RESOLUTION", column: "CoordinatingStockAdjustmentId", principalTable: "STOCK_ADJUSTMENT", principalColumn: "StockAdjustmentID", onDelete: ReferentialAction.Restrict);
            AddGuards(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS(SELECT 1 FROM STOCK_ADJUSTMENT_RESOLUTION) OR EXISTS(SELECT 1 FROM GOODS_ISSUE_INVALIDATION) OR EXISTS(SELECT 1 FROM ORDER_AMENDMENT) OR EXISTS(SELECT 1 FROM STOCK_ADJUSTMENT_IMAGE WHERE DeletePending=1) THROW 50270,'Resolution evidence exists. Down is unsafe; no business evidence was deleted.',1;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_STOCK_TRANSFER_HANDLING_HOLD; DROP TRIGGER IF EXISTS TR_ADJUSTMENT_PROPOSAL_EVIDENCE; DROP TRIGGER IF EXISTS TR_ORDER_AMENDMENT_IMMUTABLE; DROP TRIGGER IF EXISTS TR_GOODS_ISSUE_INVALIDATION_IMMUTABLE;");
            migrationBuilder.DropColumn(name: "DeletePending", table: "STOCK_ADJUSTMENT_IMAGE");
            migrationBuilder.DropForeignKey(
                name: "FK_BATCH_SALE_OFFER_STOCK_ADJUSTMENT_RepricedForAdjustmentId",
                table: "BATCH_SALE_OFFER");

            migrationBuilder.DropForeignKey(
                name: "FK_BATCH_SALE_OFFER_STOCK_ADJUSTMENT_SourceStockAdjustmentId",
                table: "BATCH_SALE_OFFER");

            migrationBuilder.DropTable(
                name: "GOODS_ISSUE_INVALIDATION");

            migrationBuilder.DropTable(
                name: "ORDER_AMENDMENT");

            migrationBuilder.DropTable(
                name: "STOCK_ADJUSTMENT_PROPOSAL");

            migrationBuilder.DropTable(
                name: "STOCK_ADJUSTMENT_RESOLUTION");

            migrationBuilder.DropIndex(
                name: "IX_BATCH_SALE_OFFER_RepricedForAdjustmentId",
                table: "BATCH_SALE_OFFER");

            migrationBuilder.DropIndex(
                name: "IX_BATCH_SALE_OFFER_SourceStockAdjustmentId",
                table: "BATCH_SALE_OFFER");

            migrationBuilder.DropColumn(
                name: "ApprovedRemainderWeightKg",
                table: "BATCH_SALE_OFFER");

            migrationBuilder.DropColumn(
                name: "RepricedForAdjustmentId",
                table: "BATCH_SALE_OFFER");

            migrationBuilder.DropColumn(
                name: "SourceStockAdjustmentId",
                table: "BATCH_SALE_OFFER");
        }
        private static void AddGuards(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_ORDER_AMENDMENT_IMMUTABLE ON ORDER_AMENDMENT AFTER UPDATE,DELETE AS
                BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM deleted) THROW 50271,'Order amendment evidence is immutable.',1; END
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_GOODS_ISSUE_INVALIDATION_IMMUTABLE ON GOODS_ISSUE_INVALIDATION AFTER UPDATE,DELETE AS
                BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM deleted) THROW 50272,'Draft invalidation evidence is immutable.',1; END
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_ADJUSTMENT_PROPOSAL_EVIDENCE ON STOCK_ADJUSTMENT_PROPOSAL AFTER UPDATE,DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.StockAdjustmentProposalId=d.StockAdjustmentProposalId
                      WHERE i.StockAdjustmentProposalId IS NULL OR d.StockAdjustmentId<>i.StockAdjustmentId OR d.Revision<>i.Revision
                      OR d.CreatedByAccountId<>i.CreatedByAccountId OR d.CreatedAt<>i.CreatedAt
                      OR (d.ProposalStatus<>'DRAFT' AND (d.OriginalWeightKg<>i.OriginalWeightKg OR d.ProposedWeightKg<>i.ProposedWeightKg
                        OR d.OriginalWholeLotPrice<>i.OriginalWholeLotPrice OR d.ProposedWholeLotPrice<>i.ProposedWholeLotPrice
                        OR d.OriginalOrderTotal<>i.OriginalOrderTotal OR d.ProposedOrderTotal<>i.ProposedOrderTotal
                        OR d.ScopeSnapshotJson<>i.ScopeSnapshotJson OR d.CustomerMessage<>i.CustomerMessage OR ISNULL(d.SentAt,'19000101')<>ISNULL(i.SentAt,'19000101')))
                      OR (d.RespondedAt IS NOT NULL AND (ISNULL(i.RespondedAt,'19000101')<>d.RespondedAt
                        OR ISNULL(d.RespondedByAccountId,0)<>ISNULL(i.RespondedByAccountId,0) OR ISNULL(d.ResponseReason,'')<>ISNULL(i.ResponseReason,''))))
                      THROW 50273,'Sent proposal and customer response evidence are immutable.',1;
                END
                """);
            // A new narrow guard; existing transfer/reservation/QC triggers remain unchanged.
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_STOCK_TRANSFER_HANDLING_HOLD ON STOCK_TRANSFER AFTER INSERT,UPDATE AS
                BEGIN SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted t WHERE t.TransferStatus NOT IN ('CANCELLED','REJECTED') AND (EXISTS
                        (SELECT 1 FROM STOCK_ADJUSTMENT a JOIN INVENTORY i ON i.InventoryID=a.InventoryID
                         LEFT JOIN STOCK_ADJUSTMENT_RESOLUTION r ON r.StockAdjustmentId=a.StockAdjustmentID
                         WHERE i.ProductBatchID=t.ProductBatchID AND (a.AdjustmentStatus='PENDING'
                           OR r.State NOT IN ('RESOLVED','CLEARED','REPLACED')))
                        OR EXISTS(SELECT 1 FROM STOCK_ADJUSTMENT_RESOLUTION w JOIN ORDER_DETAIL l ON l.PurchaseOrderID=w.PurchaseOrderId
                            JOIN PURCHASE_ORDER o ON o.PurchaseOrderID=w.PurchaseOrderId
                            WHERE o.OrderStatus NOT IN ('CANCELLED','REJECTED') AND w.State NOT IN ('RESOLVED','CLEARED','REPLACED') AND
                              (l.RequestedProductBatchID=t.ProductBatchID OR EXISTS
                                (SELECT 1 FROM INVENTORY_RESERVATION h JOIN INVENTORY s ON s.InventoryID=h.InventoryID
                                 WHERE h.OrderDetailID=l.OrderDetailID AND s.ProductBatchID=t.ProductBatchID AND h.ReleasedAt IS NULL
                                 AND h.ReservationStatus IN ('RESERVED','PARTIALLY_PICKED','PICKED'))))))
                        THROW 50274,'Batch is on stock-adjustment hold; transfer is blocked.',1;
                END
                """);
        }
    }
}
