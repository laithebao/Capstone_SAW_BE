using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoriesContractRevisionsAndEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LegalRepresentative",
                table: "SUPPLIER",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PopupHandledAt",
                table: "NOTIFICATION",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Ward",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Region",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Province",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "District",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<int>(
                name: "OwnerSupplierId",
                table: "GROWING_AREA",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(ResizeCropCode(50));

            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "CROP_TYPE",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CATEGORY",
                columns: table => new
                {
                    CategoryId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CATEGORY", x => x.CategoryId);
                    table.CheckConstraint("CK_CATEGORY_Parent", "[ParentId] IS NULL OR [ParentId] <> [CategoryId]");
                    table.ForeignKey(
                        name: "FK_CATEGORY_CATEGORY_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CATEGORY",
                        principalColumn: "CategoryId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EMAIL_OUTBOX",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventKey = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EMAIL_OUTBOX", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EMAIL_OUTBOX_ACCOUNT_AccountId",
                        column: x => x.AccountId,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SUPPLY_CONTRACT_REVISION",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SupplyContractId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    TemplateVersion = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SUPPLY_CONTRACT_REVISION", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SUPPLY_CONTRACT_REVISION_SUPPLY_CONTRACT_SupplyContractId",
                        column: x => x.SupplyContractId,
                        principalTable: "SUPPLY_CONTRACT",
                        principalColumn: "SupplyContractID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GROWING_AREA_OwnerSupplierId",
                table: "GROWING_AREA",
                column: "OwnerSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_CROP_TYPE_CategoryId",
                table: "CROP_TYPE",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CATEGORY_Code",
                table: "CATEGORY",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CATEGORY_ParentId",
                table: "CATEGORY",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_EMAIL_OUTBOX_AccountId",
                table: "EMAIL_OUTBOX",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_EMAIL_OUTBOX_EventKey_AccountId",
                table: "EMAIL_OUTBOX",
                columns: new[] { "EventKey", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EMAIL_OUTBOX_SentAt_NextAttemptAt",
                table: "EMAIL_OUTBOX",
                columns: new[] { "SentAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SUPPLY_CONTRACT_REVISION_SupplyContractId_Revision",
                table: "SUPPLY_CONTRACT_REVISION",
                columns: new[] { "SupplyContractId", "Revision" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CROP_TYPE_CATEGORY_CategoryId",
                table: "CROP_TYPE",
                column: "CategoryId",
                principalTable: "CATEGORY",
                principalColumn: "CategoryId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GROWING_AREA_SUPPLIER_OwnerSupplierId",
                table: "GROWING_AREA",
                column: "OwnerSupplierId",
                principalTable: "SUPPLIER",
                principalColumn: "SupplierID",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS(SELECT 1 FROM CROP_TYPE WHERE LEN(CropCode)>30 OR CategoryId IS NOT NULL)
                    OR EXISTS(SELECT 1 FROM CATEGORY)
                    OR EXISTS(SELECT 1 FROM EMAIL_OUTBOX)
                    OR EXISTS(SELECT 1 FROM SUPPLY_CONTRACT_REVISION)
                    OR EXISTS(SELECT 1 FROM GROWING_AREA WHERE OwnerSupplierId IS NOT NULL OR Region IS NULL OR Province IS NULL OR District IS NULL OR Ward IS NULL)
                    OR EXISTS(SELECT 1 FROM SUPPLIER WHERE LegalRepresentative IS NOT NULL AND ContactPerson NOT LIKE N'%| Đại diện PL:%')
                    THROW 51106, 'Rollback would discard new business data. Export and review before proceeding.', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_CROP_TYPE_CATEGORY_CategoryId",
                table: "CROP_TYPE");

            migrationBuilder.DropForeignKey(
                name: "FK_GROWING_AREA_SUPPLIER_OwnerSupplierId",
                table: "GROWING_AREA");

            migrationBuilder.DropTable(
                name: "CATEGORY");

            migrationBuilder.DropTable(
                name: "EMAIL_OUTBOX");

            migrationBuilder.DropTable(
                name: "SUPPLY_CONTRACT_REVISION");

            migrationBuilder.DropIndex(
                name: "IX_GROWING_AREA_OwnerSupplierId",
                table: "GROWING_AREA");

            migrationBuilder.DropIndex(
                name: "IX_CROP_TYPE_CategoryId",
                table: "CROP_TYPE");

            migrationBuilder.DropColumn(
                name: "LegalRepresentative",
                table: "SUPPLIER");

            migrationBuilder.DropColumn(
                name: "PopupHandledAt",
                table: "NOTIFICATION");

            migrationBuilder.DropColumn(
                name: "OwnerSupplierId",
                table: "GROWING_AREA");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "CROP_TYPE");

            migrationBuilder.AlterColumn<string>(
                name: "Ward",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Region",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Province",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "District",
                table: "GROWING_AREA",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.Sql(ResizeCropCode(30));
        }

        // Legacy installations implement the same uniqueness as a UNIQUE constraint;
        // model-created fixtures use an index. Preserve either form, never disable it.
        private static string ResizeCropCode(int length) => $$"""
            DECLARE @cropCodeKeyConstraint bit = CASE WHEN EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.CROP_TYPE') AND name='UQ_CROP_TYPE_Code') THEN 1 ELSE 0 END;
            IF @cropCodeKeyConstraint=1 ALTER TABLE dbo.CROP_TYPE DROP CONSTRAINT UQ_CROP_TYPE_Code;
            ELSE IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.CROP_TYPE') AND name='UQ_CROP_TYPE_Code') DROP INDEX UQ_CROP_TYPE_Code ON dbo.CROP_TYPE;
            ALTER TABLE dbo.CROP_TYPE ALTER COLUMN CropCode nvarchar({{length}}) NOT NULL;
            IF @cropCodeKeyConstraint=1 ALTER TABLE dbo.CROP_TYPE ADD CONSTRAINT UQ_CROP_TYPE_Code UNIQUE(CropCode);
            ELSE CREATE UNIQUE INDEX UQ_CROP_TYPE_Code ON dbo.CROP_TYPE(CropCode);
            """;
    }
}
