using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task Migration_has_trusted_foreign_keys_and_refuses_down_when_resolution_evidence_exists()
    {
        Assert.StartsWith("SAW_UC28_Test_", new SqlConnectionStringBuilder(F.ConnectionString).InitialCatalog);
        var scenario = await Scenario(); var a = await Service().CreateAsync(await Request(scenario.Batch), F.ActorId, default);
        await using var db = F.Context();
        Assert.Contains("20261017204518_AddStockAdjustmentOrderResolution", await db.Database.GetAppliedMigrationsAsync());
        var unsafeKeys = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id IN
              (OBJECT_ID('STOCK_ADJUSTMENT_RESOLUTION'),OBJECT_ID('STOCK_ADJUSTMENT_PROPOSAL'),OBJECT_ID('ORDER_AMENDMENT'),OBJECT_ID('GOODS_ISSUE_INVALIDATION'))
              AND (is_disabled=1 OR is_not_trusted=1)
            """).SingleAsync(); Assert.Equal(0, unsafeKeys);
        var rollback = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261016190107_AllowDisposedProductBatchStatus"));
        Assert.Equal(50270, rollback.Number);
        Assert.Contains("20261017204518_AddStockAdjustmentOrderResolution", await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal("PENDING", (await ReadEntity(a.Summary.Id)).AdjustmentStatus);
        Assert.True(await db.StockAdjustmentResolutions.AnyAsync(r => r.StockAdjustmentId == a.Summary.Id));
    }
}
