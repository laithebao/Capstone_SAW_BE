using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SAW.Infrastructure.Migrations;
using SAW.Test.Infrastructure.Picking;

namespace SAW.Test.Infrastructure.GoodsIssues;

public sealed class GoodsIssueSqlFixture : IAsyncLifetime
{
    public PickingSqlFixture Picking { get; } = new();
    public async Task InitializeAsync()
    {
        await Picking.InitializeAsync();
        var source = Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION");
        if (string.IsNullOrEmpty(source)) return;
        try
        {
            await using var db = Picking.Database.Context();
            await WarehouseWorkflowTestSchema.InstallAsync(db, "SAW.Test.Infrastructure.GoodsIssues.GoodsIssueSchema.sql");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE GOODS_ISSUE ALTER COLUMN IssuedAt datetime2(0) NOT NULL; ALTER TABLE GOODS_ISSUE ALTER COLUMN CommittedAt datetime2(0) NULL;");
            // Apply the real migration's reviewed SQL over the bundled fixture baseline.
            // Migration/history paths have separate tests; do not write EF history here.
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var operation in new FixGoodsIssueWorkflowTriggers().UpOperations.Cast<SqlOperation>())
                await db.Database.ExecuteSqlRawAsync(operation.Sql);
            await transaction.CommitAsync();
        }
        catch { await Picking.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Picking.DisposeAsync();
}
