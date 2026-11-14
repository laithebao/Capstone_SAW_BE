using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Infrastructure.Persistence;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.Picking;

[Xunit.Sdk.TraitDiscoverer("SAW.Test.SqlTraitDiscoverer", "SAW.Test")]
public sealed class ReceivingSqlTheoryAttribute : TheoryAttribute, Xunit.Sdk.ITraitAttribute
{
    public ReceivingSqlTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION")))
            Skip = "Set SAW_TEST_SQL_CONNECTION to run isolated SQL Server tests.";
    }
}

public sealed class PickingTriggerMigrationTests
{
    private const string Previous = "20261108090000_MakeLegacySpoilageRiskScoreOptional";
    private const string Target = "20261112160000_EnsurePickingHistorySync";

    private static Task<string?> Definition(AppDbContext db) => db.Database.SqlQueryRaw<string?>(
        "SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.TR_PICKING_HISTORY_SYNC')) AS Value").SingleAsync();

    [ReceivingSqlFact]
    public async Task Missing_trigger_is_installed_once_and_existing_definition_and_rows_survive_adoption_and_down()
    {
        var fixture = new ReceivingSqlFixture(useMigrationBaseline: true);
        await fixture.InitializeAsync();
        try
        {
            await fixture.BatchAsync();
            await using var db = fixture.Context();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(Previous);
            var before = await db.Database.SqlQueryRaw<string>(
                "SELECT (SELECT * FROM PRODUCT_BATCH ORDER BY ProductBatchID FOR JSON PATH,INCLUDE_NULL_VALUES) AS Value").SingleAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_PICKING_HISTORY_SYNC");
            await migrator.MigrateAsync(Target);
            var created = await Definition(db);
            Assert.NotNull(created);
            Assert.Contains("THROW 50105", created);
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
                "SELECT CONVERT(int,is_disabled) AS Value FROM sys.triggers WHERE object_id=OBJECT_ID('dbo.TR_PICKING_HISTORY_SYNC')").SingleAsync());
            await migrator.MigrateAsync(Target);
            Assert.Single(await db.Database.GetAppliedMigrationsAsync(), m => m == Target);
            await migrator.MigrateAsync(Previous);
            Assert.Equal(created, await Definition(db));
            await migrator.MigrateAsync(Target);
            Assert.Equal(created, await Definition(db));
            Assert.Equal(before, await db.Database.SqlQueryRaw<string>(
                "SELECT (SELECT * FROM PRODUCT_BATCH ORDER BY ProductBatchID FOR JSON PATH,INCLUDE_NULL_VALUES) AS Value").SingleAsync());
            Assert.Empty(await db.PickingHistories.ToListAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [ReceivingSqlTheory]
    [InlineData("unknown", 50310)]
    [InlineData("encrypted", 50310)]
    [InlineData("disabled", 50311)]
    public async Task Unreviewed_or_disabled_trigger_is_rejected_without_overwrite_or_history(string variant, int error)
    {
        var fixture = new ReceivingSqlFixture(useMigrationBaseline: true);
        await fixture.InitializeAsync();
        try
        {
            await using var db = fixture.Context();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(Previous);
            var definition = (await Definition(db))!;
            if (variant == "disabled") await db.Database.ExecuteSqlRawAsync("DISABLE TRIGGER dbo.TR_PICKING_HISTORY_SYNC ON dbo.PICKING_HISTORY");
            else
            {
                var changed = definition.Replace("CREATE TRIGGER", "CREATE OR ALTER TRIGGER");
                changed = variant == "unknown" ? changed.Replace("SET NOCOUNT ON;", "SET NOCOUNT ON; -- MEMBER CHANGE")
                    : changed.Replace("AFTER INSERT", "WITH ENCRYPTION\nAFTER INSERT");
                await db.Database.ExecuteSqlRawAsync(changed);
            }
            var before = await Definition(db);
            var failure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(Target));
            Assert.Equal(error, failure.Number);
            Assert.DoesNotContain(Target, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(before, await Definition(db));
            Assert.Equal(variant == "disabled" ? 1 : 0, await db.Database.SqlQueryRaw<int>(
                "SELECT CONVERT(int,is_disabled) AS Value FROM sys.triggers WHERE object_id=OBJECT_ID('dbo.TR_PICKING_HISTORY_SYNC')").SingleAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }
}
