using Microsoft.Data.SqlClient;
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
            await using var live = new SqlConnection(source); await live.OpenAsync();
            await using var command = live.CreateCommand();
            command.CommandText = """
                SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name IN ('TR_GOODS_ISSUE_COMMITTED_IMMUTABLE','TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK');
                SELECT OBJECT_NAME(parent_object_id),name,definition FROM sys.check_constraints WHERE OBJECT_NAME(parent_object_id) IN ('GOODS_ISSUE','GOODS_ISSUE_DETAIL','PRODUCT_BATCH');
                """;
            var triggers = new List<string>(); var checks = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) triggers.Add(reader.GetString(0));
                await reader.NextResultAsync();
                while (await reader.ReadAsync()) checks.Add($"IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE name='{reader.GetString(1)}') ALTER TABLE [{reader.GetString(0)}] ADD CONSTRAINT [{reader.GetString(1)}] CHECK {reader.GetString(2)}");
            }
            Assert.Equal(2, triggers.Count);
            await using var db = Picking.Database.Context();
            foreach (var check in checks) await db.Database.ExecuteSqlRawAsync(check);
            foreach (var trigger in triggers) await db.Database.ExecuteSqlRawAsync(trigger);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE GOODS_ISSUE ALTER COLUMN IssuedAt datetime2(0) NOT NULL; ALTER TABLE GOODS_ISSUE ALTER COLUMN CommittedAt datetime2(0) NULL;");
            // Workflow fixture uses the migration's immutable SQL; migration/history paths have
            // separate tests. No independent SQL source and no writes to EF migration history here.
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var operation in new FixGoodsIssueWorkflowTriggers().UpOperations.Cast<SqlOperation>())
                await db.Database.ExecuteSqlRawAsync(operation.Sql);
            await transaction.CommitAsync();
        }
        catch { await Picking.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Picking.DisposeAsync();
}
