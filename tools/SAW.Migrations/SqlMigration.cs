using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SAW.Migrations;

public static class SqlMigration
{
    public static SqlConnectionStringBuilder Connection(string secret, string server, string database)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Missing environment secret AZURE_SQL_MIGRATION_CONNECTION_STRING.");
        var builder = new SqlConnectionStringBuilder(secret);
        if (!string.Equals(builder.DataSource, $"tcp:{server},1433", StringComparison.OrdinalIgnoreCase) ||
            builder.InitialCatalog != database || !server.EndsWith(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
            builder.Encrypt != SqlConnectionEncryptOption.Mandatory || builder.TrustServerCertificate ||
            builder.IntegratedSecurity || builder.PersistSecurityInfo)
            throw new InvalidOperationException("Migration connection must target the configured Azure database using tcp:<server>,1433; Encrypt=True; TrustServerCertificate=False; Persist Security Info=False.");
        builder.ConnectTimeout = 30;
        builder.ConnectRetryCount = 0;
        builder.Pooling = false;
        return builder;
    }

    public static async Task<string[]> History(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT [MigrationId] FROM [dbo].[__EFMigrationsHistory] ORDER BY [MigrationId]", connection);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    public static async Task OpenAfterFirewallUpdate(SqlConnection connection)
    {
        // Retry ONLY a connection rejected by a not-yet-propagated firewall rule.
        // No migration SQL has run here; never retry an executing migration blindly.
        for (var attempt = 0; ; attempt++)
        {
            try { await connection.OpenAsync(); return; }
            catch (SqlException error) when (error.Number == 40615 && attempt < 20)
            {
                Console.WriteLine("Waiting for the temporary Azure SQL firewall rule to propagate.");
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }
    }

    public static async Task Apply(SqlConnection connection, MigrationPlan plan, string directory,
        string recordFile, Func<Task<bool>>? stillCurrent = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["status"] = "failed", ["commit"] = plan.Commit, ["run_id"] = plan.RunId,
            ["run_attempt"] = plan.RunAttempt, ["required_migration"] = plan.Head,
            ["database"] = connection.Database, ["applied_by_run"] = new List<string>()
        };
        try
        {
            // EF 8 has no migration lock. Keep one SQL session and an application lock
            // across the complete sequence, including per-migration BEGIN/COMMIT batches.
            await Execute(connection, """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=N'SAW-schema-migration',
                    @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=60000;
                IF @result < 0 THROW 51000, 'Cannot acquire SAW migration lock.', 1;
                SET XACT_ABORT ON;
                """);
            var before = await History(connection);
            var pending = plan.Pending(before);
            record["before"] = before.LastOrDefault();
            foreach (var step in pending)
            {
                if (stillCurrent is not null && !await stillCurrent())
                    throw new InvalidOperationException("Main advanced before migration execution. Rerun the latest release.");
                Console.WriteLine($"Applying reviewed migration: {step.Id}");
                // Recheck the bytes immediately before execution; never regenerate SQL here.
                var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, step.File));
                if (MigrationPlan.Hash(bytes) != step.Sha256) throw new InvalidOperationException("Migration SQL changed after review.");
                foreach (var batch in MigrationPlan.ParseBatches(System.Text.Encoding.UTF8.GetString(bytes)))
                    await Execute(connection, batch);
                await using (var transactionCount = new SqlCommand("SELECT @@TRANCOUNT", connection))
                    if (Convert.ToInt32(await transactionCount.ExecuteScalarAsync()) != 0)
                        throw new InvalidOperationException("Migration left an uncommitted transaction. Refusing to deploy the API.");
                var afterStep = await History(connection);
                var expected = plan.Migrations.Take(Array.IndexOf(plan.Migrations, step.Id) + 1);
                if (!afterStep.SequenceEqual(expected)) throw new InvalidOperationException("Migration did not leave the expected EF history.");
                ((List<string>)record["applied_by_run"]!).Add(step.Id);
            }
            var after = await History(connection);
            if (!after.SequenceEqual(plan.Migrations)) throw new InvalidOperationException("Azure database did not reach this release's migration head.");
            record["after"] = after.Last();
            record["status"] = "verified";
            Console.WriteLine($"Database verified at {plan.Head}; applied {pending.Length} migration(s).");
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(recordFile))!);
            await File.WriteAllTextAsync(recordFile, JsonSerializer.Serialize(record, MigrationPlan.Json));
            // No Down migration: a prior migration may already have committed.
            // Roll back only an open transaction and release this session's lock.
            try { await Execute(connection, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; EXEC sys.sp_releaseapplock @Resource=N'SAW-schema-migration', @LockOwner=N'Session';"); }
            catch (SqlException) { /* Closing the nonpooled connection also releases a session lock. */ }
        }
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync();
    }
}
