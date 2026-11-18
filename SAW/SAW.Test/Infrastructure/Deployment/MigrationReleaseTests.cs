using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Infrastructure.Persistence;
using SAW.Migrations;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.Deployment;

public sealed class MigrationReleaseTests : IDisposable
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Baseline = "20261112160000_EnsurePickingHistorySync";
    private const string Next = "20261201000000_ReleaseProbe";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "saw-migration-" + Guid.NewGuid().ToString("N"));

    public MigrationReleaseTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void Generate_is_offline_and_current_import_needs_no_schema_changes()
    {
        var plan = MigrationPlan.Generate(directory, Baseline, Commit, "123", "1");
        Assert.Contains(Baseline, plan.Migrations);
        Assert.Empty(plan.Pending(plan.Migrations));
        Assert.Equal(plan.Head, MigrationPlan.Load(directory, Commit, "123", "1").Head);
        Assert.Throws<InvalidOperationException>(() => MigrationPlan.Load(directory, Commit, "123", "2"));
    }

    [Fact]
    public void Future_head_is_accepted_without_advancing_an_approval_constant()
    {
        var plan = Plan("SELECT 1;\nGO\n");
        plan.Validate(directory, Commit, "123", "1");
        Assert.Equal(Next, Assert.Single(plan.Pending([Baseline])).Id);
        Assert.Empty(plan.Pending([Baseline, Next]));
    }

    [Fact]
    public void Rejects_empty_older_gapped_and_unknown_history()
    {
        var plan = Plan("SELECT 1;");
        foreach (var history in new[] { Array.Empty<string>(), new[] { Next },
            new[] { Baseline, "20261202000000_Unknown" }, new[] { Baseline, Next, "20261202000000_Newer" } })
            Assert.Throws<InvalidOperationException>(() => plan.Pending(history));
    }

    [Fact]
    public void Changed_sql_or_missing_intermediate_script_is_rejected()
    {
        var plan = Plan("SELECT 1;");
        File.AppendAllText(Path.Combine(directory, Next + ".sql"), "SELECT 2;");
        Assert.Throws<InvalidOperationException>(() => plan.Validate(directory, Commit, "123", "1"));
        Assert.Throws<InvalidOperationException>(() => (plan with { Steps = [] }).Validate(directory, Commit, "123", "1"));
    }

    [Fact]
    public void Batch_parser_keeps_GO_inside_strings_and_comments()
    {
        const string sql = "SELECT N'line one\nGO\nline two';\nGO\n/*\nGO\n*/\nSELECT 2;\nGO\n";
        var batches = MigrationPlan.ParseBatches(sql);
        Assert.Equal(2, batches.Length);
        Assert.Contains("line one\nGO\nline two", batches[0]);
        Assert.Contains("SELECT 2", batches[1]);
        Assert.Throws<InvalidOperationException>(() => MigrationPlan.ParseBatches("THIS IS INVALID SQL;"));
    }

    [Fact]
    public void Every_existing_migration_script_parses_including_dynamic_trigger_sql()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=offline;User Id=unused;Password=unused").Options);
        var migrations = db.Database.GetMigrations().ToArray();
        for (var i = 1; i < migrations.Length; i++)
            MigrationPlan.ParseBatches(db.GetService<IMigrator>().GenerateScript(migrations[i - 1], migrations[i]));
    }

    [Theory]
    [InlineData("Server=tcp:other.database.windows.net,1433;Database=SmartAgriWarehouseDB;User Id=test;Password=unused;Encrypt=True")]
    [InlineData("Server=tcp:saw.database.windows.net,1433;Database=master;User Id=test;Password=unused;Encrypt=True")]
    [InlineData("Server=tcp:saw.database.windows.net,1433;Database=SmartAgriWarehouseDB;User Id=test;Password=unused;Encrypt=False")]
    [InlineData("Server=tcp:saw.database.windows.net,1433;Database=SmartAgriWarehouseDB;User Id=test;Password=unused;Encrypt=True;TrustServerCertificate=True")]
    public void Wrong_database_or_untrusted_transport_is_rejected(string connection)
        => Assert.Throws<InvalidOperationException>(() => SqlMigration.Connection(connection, "saw.database.windows.net", "SmartAgriWarehouseDB"));

    [ReceivingSqlFact]
    public async Task Reviewed_sql_applies_once_and_a_second_run_only_verifies_history()
    {
        await WithDatabase(async connection =>
        {
            var plan = Plan($"""
                BEGIN TRANSACTION;
                GO
                CREATE TABLE dbo.RELEASE_PROBE (Id int NOT NULL PRIMARY KEY);
                INSERT dbo.RELEASE_PROBE VALUES (1);
                GO
                INSERT dbo.__EFMigrationsHistory VALUES (N'{Next}', N'8.0.0');
                COMMIT;
                GO
                """);
            var record = Path.Combine(directory, "result.json");
            await SqlMigration.Apply(connection, plan, directory, record);
            await SqlMigration.Apply(connection, plan, directory, record);
            Assert.Equal(new[] { Baseline, Next }, await SqlMigration.History(connection));
            using var result = JsonDocument.Parse(File.ReadAllText(record));
            Assert.Equal("verified", result.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, result.RootElement.GetProperty("applied_by_run").GetArrayLength());
        });
    }

    [ReceivingSqlFact]
    public async Task Sql_failure_rolls_back_open_transaction_and_never_emits_verified_receipt()
    {
        await WithDatabase(async connection =>
        {
            var plan = Plan($"""
                BEGIN TRANSACTION;
                GO
                CREATE TABLE dbo.RELEASE_PROBE (Id int NOT NULL);
                GO
                THROW 51000, 'Deliberate test failure', 1;
                GO
                INSERT dbo.__EFMigrationsHistory VALUES (N'{Next}', N'8.0.0');
                COMMIT;
                GO
                """);
            var record = Path.Combine(directory, "result.json");
            await Assert.ThrowsAsync<SqlException>(() => SqlMigration.Apply(connection, plan, directory, record));
            Assert.Equal(new[] { Baseline }, await SqlMigration.History(connection));
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE name=N'RELEASE_PROBE'", connection);
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
            using var result = JsonDocument.Parse(File.ReadAllText(record));
            Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
        });
    }

    [ReceivingSqlFact]
    public async Task Uncommitted_history_is_not_accepted_as_a_success()
    {
        await WithDatabase(async connection =>
        {
            var plan = Plan($"BEGIN TRANSACTION; INSERT dbo.__EFMigrationsHistory VALUES (N'{Next}', N'8.0.0');");
            var record = Path.Combine(directory, "result.json");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SqlMigration.Apply(connection, plan, directory, record));
            Assert.Contains("uncommitted", error.Message);
            Assert.Equal(new[] { Baseline }, await SqlMigration.History(connection));
            using var result = JsonDocument.Parse(File.ReadAllText(record));
            Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
        });
    }

    [ReceivingSqlFact]
    public async Task Superseded_commit_is_rejected_before_any_schema_change()
    {
        await WithDatabase(async connection =>
        {
            var plan = Plan("CREATE TABLE dbo.RELEASE_PROBE (Id int NOT NULL);");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SqlMigration.Apply(
                connection, plan, directory, Path.Combine(directory, "result.json"), () => Task.FromResult(false)));
            Assert.Contains("Main advanced", error.Message);
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE name=N'RELEASE_PROBE'", connection);
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        });
    }

    private MigrationPlan Plan(string sql)
    {
        var file = Next + ".sql";
        File.WriteAllText(Path.Combine(directory, file), sql);
        return new(Commit, "123", "1", Baseline, [Baseline, Next],
            [new(Next, file, MigrationPlan.Hash(File.ReadAllBytes(Path.Combine(directory, file))))]);
    }

    private static async Task WithDatabase(Func<SqlConnection, Task> test)
    {
        var name = "SAW_Migration_Release_Test_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION"))
            { InitialCatalog = "master", Pooling = false };
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync();
        await using (var create = new SqlCommand($"CREATE DATABASE [{name}]", master)) await create.ExecuteNonQueryAsync();
        try
        {
            builder.InitialCatalog = name;
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using (var init = new SqlCommand($"CREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL); INSERT dbo.__EFMigrationsHistory VALUES (N'{Baseline}', N'8.0.0');", connection))
                await init.ExecuteNonQueryAsync();
            await test(connection);
        }
        finally
        {
            await using var drop = new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]", master);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
