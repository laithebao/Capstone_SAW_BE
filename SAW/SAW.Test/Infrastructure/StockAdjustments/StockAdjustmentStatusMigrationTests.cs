using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed class StockAdjustmentStatusMigrationTests
{
    [ReceivingSqlFact]
    public async Task Status_constraint_migration_up_down_repeated_up_preserves_all_business_rows_and_refuses_unsafe_down()
    {
        var f=new ReceivingSqlFixture(useMigrationBaseline:true);await f.InitializeAsync();
        try
        {
            await using var db=f.Context();var migrator=db.GetService<IMigrator>();const string previous="20261016163023_AddStockAdjustmentRequestScope",target="20261016190107_AllowDisposedProductBatchStatus";
            await migrator.MigrateAsync(previous);var batch=await f.BatchAsync();var auditCount=await db.AuditLogs.CountAsync();
            await migrator.MigrateAsync(target);await migrator.MigrateAsync(target);Assert.Equal(auditCount,await db.AuditLogs.CountAsync());
            await migrator.MigrateAsync(previous);Assert.DoesNotContain(target,await db.Database.GetAppliedMigrationsAsync());await migrator.MigrateAsync(target);
            // Exercise actual SQL constraint for every old status and new DISPOSED, on a dedicated fixture row.
            foreach(var status in new[]{"PENDING_PREDECLARATION","SUBMITTED","PENDING_QC","APPROVED_FOR_STORAGE","QUARANTINE","REJECTED","RECEIVED","IN_STOCK","RESERVED","PARTIALLY_ISSUED","ISSUED","CANCELLED","DISPOSED"})
                await db.ProductBatches.Where(b=>b.ProductBatchId==batch.ProductBatchId).ExecuteUpdateAsync(x=>x.SetProperty(b=>b.BatchStatus,status));
            var e=await Assert.ThrowsAsync<SqlException>(()=>migrator.MigrateAsync(previous));Assert.Equal(50236,e.Number);Assert.Contains(target,await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal("DISPOSED",await db.ProductBatches.Where(b=>b.ProductBatchId==batch.ProductBatchId).Select(b=>b.BatchStatus).SingleAsync());
            Assert.Equal(auditCount,await db.AuditLogs.CountAsync());Assert.Empty(await db.Inventories.ToListAsync());
        }
        finally{await f.DisposeAsync();}
    }
    [ReceivingSqlFact]
    public async Task Migration_refuses_unknown_member_status_definition_without_replacing_it()
    {
        var f=new ReceivingSqlFixture(useMigrationBaseline:true);await f.InitializeAsync();
        try
        {
            await using var db=f.Context();var m=db.GetService<IMigrator>();await m.MigrateAsync("20261016163023_AddStockAdjustmentRequestScope");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE PRODUCT_BATCH DROP CONSTRAINT CK_PRODUCT_BATCH_Status; ALTER TABLE PRODUCT_BATCH ADD CONSTRAINT CK_PRODUCT_BATCH_Status CHECK (BatchStatus IN ('SUBMITTED','MEMBER_NEW_STATE'))");
            var e=await Assert.ThrowsAsync<SqlException>(()=>m.MigrateAsync());Assert.Equal(50235,e.Number);
            var definition=await db.Database.SqlQueryRaw<string>("SELECT definition AS Value FROM sys.check_constraints WHERE name='CK_PRODUCT_BATCH_Status'").SingleAsync();Assert.Contains("MEMBER_NEW_STATE",definition);Assert.DoesNotContain("DISPOSED",definition);
        }
        finally{await f.DisposeAsync();}
    }
}
