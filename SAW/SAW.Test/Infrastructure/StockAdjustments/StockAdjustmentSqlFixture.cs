using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed class StockAdjustmentSqlFixture : IAsyncLifetime
{
    public ReceivingSqlFixture Database { get; } = new(useMigrationBaseline: true);
    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        var source = Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION");
        if (string.IsNullOrEmpty(source)) return;
        try
        {
            // The new schema/guard triggers have already been installed through actual EF
            // migrations. Read only legacy constraints/triggers from dev for regression fidelity.
            await using var live = new SqlConnection(source); await live.OpenAsync();
            await using var command = live.CreateCommand();
            command.CommandText = """
                SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name IN
                ('TR_PICKING_HISTORY_SYNC','TR_PICKING_HISTORY_IMMUTABLE','TR_INVENTORY_RESERVATION_INTEGRITY',
                 'TR_PURCHASE_ORDER_STATUS_HISTORY','TR_ORDER_STATUS_HISTORY_IMMUTABLE','TR_PRODUCT_BATCH_STATUS_HISTORY',
                 'TR_BATCH_STATUS_HISTORY_IMMUTABLE','TR_AUDIT_LOG_IMMUTABLE','TR_GOODS_ISSUE_COMMITTED_IMMUTABLE');
                SELECT OBJECT_NAME(parent_object_id),name,definition FROM sys.check_constraints
                WHERE OBJECT_NAME(parent_object_id) IN ('INVENTORY','INVENTORY_RESERVATION','PICKING_HISTORY','ORDER_DETAIL','PURCHASE_ORDER','STOCK_ADJUSTMENT','PRODUCT_BATCH','AUDIT_LOG');
                """;
            var commands = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) commands.Add(System.Text.RegularExpressions.Regex.Replace(reader.GetString(0), @"\bCREATE\s+(?:OR\s+ALTER\s+)?TRIGGER\b", "CREATE OR ALTER TRIGGER", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                await reader.NextResultAsync();
                while (await reader.ReadAsync()) commands.Add($"IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE name='{reader.GetString(1)}') ALTER TABLE [{reader.GetString(0)}] ADD CONSTRAINT [{reader.GetString(1)}] CHECK {reader.GetString(2)}");
            }
            await using var db = Database.Context();
            foreach (var sql in commands) await db.Database.ExecuteSqlRawAsync(sql);
            Assert.Contains("20261016163023_AddStockAdjustmentRequestScope", await db.Database.GetAppliedMigrationsAsync());
        }
        catch { await Database.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Database.DisposeAsync();
}
