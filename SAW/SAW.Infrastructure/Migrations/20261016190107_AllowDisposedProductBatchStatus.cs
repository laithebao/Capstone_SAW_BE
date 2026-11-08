using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowDisposedProductBatchStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ChangeConstraint(migrationBuilder, allowDisposed: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ChangeConstraint(migrationBuilder, allowDisposed: false);
        }

        // Verified against the actual local constraint. Do not overwrite an unknown member
        // definition; rollback never rewrites DISPOSED business rows into a different status.
        private const string Before = "[BatchStatus]=N'CANCELLED' OR [BatchStatus]=N'ISSUED' OR [BatchStatus]=N'PARTIALLY_ISSUED' OR [BatchStatus]=N'RESERVED' OR [BatchStatus]=N'IN_STOCK' OR [BatchStatus]=N'RECEIVED' OR [BatchStatus]=N'REJECTED' OR [BatchStatus]=N'QUARANTINE' OR [BatchStatus]=N'APPROVED_FOR_STORAGE' OR [BatchStatus]=N'PENDING_QC' OR [BatchStatus]=N'SUBMITTED' OR [BatchStatus]=N'PENDING_PREDECLARATION'";
        private const string After = Before + " OR [BatchStatus]=N'DISPOSED'";
        private static void ChangeConstraint(MigrationBuilder migrationBuilder, bool allowDisposed)
        {
            var rollbackGuard = allowDisposed ? "" : "IF EXISTS(SELECT 1 FROM dbo.PRODUCT_BATCH WHERE BatchStatus=N'DISPOSED') THROW 50236,'Cannot remove DISPOSED while disposed batches exist. No business rows were changed.',1;";
            migrationBuilder.Sql($"""
                DECLARE @actual nvarchar(max)=(SELECT definition FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.PRODUCT_BATCH') AND name='CK_PRODUCT_BATCH_Status');
                DECLARE @before nvarchar(max)=N'{Before.Replace("'", "''")}';
                DECLARE @after nvarchar(max)=N'{After.Replace("'", "''")}';
                SELECT @actual=UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@actual,CHAR(13),''),CHAR(10),''),CHAR(9),''),' ',''),'(',''),')',''),'[',''),']','')),
                    @before=UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@before,' ',''),'(',''),')',''),'[',''),']','')),
                    @after=UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@after,' ',''),'(',''),')',''),'[',''),']',''));
                IF @actual IS NOT NULL AND @actual NOT IN (@before,@after)
                    THROW 50235,'Product batch status constraint differs from the verified definition. Review local changes before migration.',1;
                {rollbackGuard}
                IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.PRODUCT_BATCH') AND name='CK_PRODUCT_BATCH_Status')
                    ALTER TABLE dbo.PRODUCT_BATCH DROP CONSTRAINT CK_PRODUCT_BATCH_Status;
                ALTER TABLE dbo.PRODUCT_BATCH WITH CHECK ADD CONSTRAINT CK_PRODUCT_BATCH_Status CHECK ({(allowDisposed ? After : Before)});
                """);
        }
    }
}
