using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SAW.Domain.Entities;
using SAW.Infrastructure.Migrations;
using SAW.Infrastructure.Persistence;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.GoodsIssues;

public sealed class GoodsIssueMigrationTests
{
    private const string Previous = "20261007134920_NormalizePurchaseOrderHistoryUtc";
    private const string Target = "20261016153240_FixGoodsIssueWorkflowTriggers";
    private static string Canonical(string sql) => Regex.Replace(sql, @"\s+", "").ToUpperInvariant()
        .Replace("CREATEORALTERTRIGGER", "CREATETRIGGER").Replace("ALTERTRIGGER", "CREATETRIGGER");
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    [Fact]
    public void Migration_contains_only_guard_and_two_trigger_definitions_and_no_schema_operations()
    {
        var migration = new FixGoodsIssueWorkflowTriggers();
        foreach (var operations in new[] { migration.UpOperations, migration.DownOperations })
        {
            Assert.Equal(3, operations.Count);
            Assert.All(operations, operation => Assert.IsType<SqlOperation>(operation));
            var sql = operations.Cast<SqlOperation>().ToArray();
            Assert.Contains("Review local changes", sql[0].Sql);
            Assert.Matches(@"(?m)^\s*CREATE OR ALTER TRIGGER", sql[1].Sql);
            Assert.Contains("TR_INVENTORY_RESERVATION_SYNC", sql[1].Sql);
            Assert.Contains("TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK", sql[2].Sql);
            Assert.All(sql, operation => Assert.False(Regex.IsMatch(operation.Sql, @"^\s*GO\s*$", RegexOptions.Multiline)));
        }
    }
    private sealed class Database : IAsyncDisposable
    {
        private readonly string name = "SAW_GoodsIssueMigration_Test_" + Guid.NewGuid().ToString("N");
        private string master = "";
        public string ConnectionString { get; private set; } = "";
        public AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);
        public async Task CreateAsync()
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION")) { InitialCatalog = "master" };
            master = builder.ConnectionString;
            await using var connection = new SqlConnection(master); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{name}]"; await command.ExecuteNonQueryAsync();
            builder.InitialCatalog = name; ConnectionString = builder.ConnectionString;
        }
        public async ValueTask DisposeAsync()
        {
            if (master.Length == 0) return;
            Assert.Matches("^SAW_GoodsIssueMigration_Test_[a-f0-9]{32}$", name);
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(master); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await command.ExecuteNonQueryAsync();
        }
    }
    private static async Task BootstrapLegacySchemaAsync(AppDbContext db)
    {
        // InitialCreate.Up is empty in the shared history: an exported legacy schema is required.
        // For an empty, isolated fixture generate that legacy physical schema from its own target
        // model, then run ALL real EF migrations. No history rows are inserted by test code.
        var model = db.GetService<IModelRuntimeInitializer>().Initialize(new InitialCreate().TargetModel, designTime: true);
        var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations, model);
        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command.CommandText.Replace("ON DELETE CASCADE", "ON DELETE NO ACTION"));
        // The legacy database supplies these computed definitions; InitialCreate's target
        // model only marks them generated. Reproduce that external baseline in this fixture.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE INVENTORY DROP COLUMN AvailableQuantity;
            ALTER TABLE INVENTORY ADD AvailableQuantity AS (QuantityOnHand - ReservedQuantity) PERSISTED;
            ALTER TABLE ORDER_DETAIL DROP COLUMN LineSubtotal,LineTotal;
            ALTER TABLE ORDER_DETAIL ADD LineSubtotal AS CONVERT(decimal(18,2),RequestedQuantity*UnitPrice) PERSISTED;
            ALTER TABLE ORDER_DETAIL ADD LineTotal AS CONVERT(decimal(18,2),RequestedQuantity*UnitPrice+TaxAmount) PERSISTED;
            """);
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        // Restore verified legacy definitions after the baseline EF chain. Other warehouse
        // trigger behavior is covered by workflow fixtures loaded from the actual dev schema.
        foreach (var operation in new FixGoodsIssueWorkflowTriggers().DownOperations.Skip(1).Cast<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(operation.Sql);
    }
    private static async Task<IReadOnlyList<string>> DefinitionsAsync(AppDbContext db) =>
        await db.Database.SqlQueryRaw<string>("SELECT definition AS Value FROM sys.sql_modules WHERE object_id IN (OBJECT_ID('dbo.TR_INVENTORY_RESERVATION_SYNC'),OBJECT_ID('dbo.TR_GOODS_ISSUE_DETAIL_INTEGRITY_CHECK')) ORDER BY object_id")
            .ToListAsync();
    private static async Task SeedProbeAsync(AppDbContext db)
    {
        var role = new Role { RoleCode = "MIGRATION_TEST", RoleName = "Fixture", IsActive = true };
        var account = new Account { Role = role, Username = "migration", Email = "migration@test.invalid", FullName = "Migration fixture", PasswordHash = "unused", AccountStatus = "ACTIVE" };
        var supplier = new Supplier { Account = account, SupplierCode = "MIG", SupplierName = "Fixture supplier", TaxCode = "TEST", ContactPerson = "Fixture", Address = "Fixture" };
        var buyer = new Distributor { Account = account, DistributorCode = "MIG", DistributorName = "Fixture distributor", TaxCode = "TEST", ProfileStatus = "ACTIVE" };
        var crop = new CropType { CropCode = "MIG", CropName = "Fixture", CategoryName = "Fixture", IsActive = true };
        var area = new GrowingArea { AreaName = "Fixture", Region = "Fixture", Province = "Fixture", District = "Fixture", Ward = "Fixture" };
        var batch = new ProductBatch { BatchCode = "MIGRATION-PROBE", Supplier = supplier, CropType = crop, GrowingArea = area,
            ProductName = "Migration fixture", HarvestDate = new(2026, 1, 1), DeclaredQuantity = 100, WeightInKg = 100,
            VerifiedQuantity = 100, VerifiedWeightInKg = 100, Unit = "kg", BatchStatus = "IN_STOCK", QualityGrade = "E", CreatedAt = DateTime.UtcNow };
        var stock = new Inventory { ProductBatch = batch, WarehouseLocation = new WarehouseLocation { LocationCode = "MIG", ZoneName = "Test", LocationStatus = "ACTIVE" }, Unit = "kg", QuantityOnHand = 100, LastUpdatedAt = DateTime.UtcNow };
        var order = new PurchaseOrder { Distributor = buyer, OrderCode = "MIGRATION-ORDER", OrderStatus = "PARTIALLY_PICKED", DeliveryAddress = "Fixture", CreatedAt = DateTime.UtcNow, ApprovedByAccount = account, ApprovedAt = DateTime.UtcNow };
        var line = new OrderDetail { PurchaseOrder = order, CropType = crop, RequestedProductBatch = batch, RequestedQuantity = 1, ApprovedQuantity = 1, Unit = "Lô", RequestedWeightKg = 100, ApprovedWeightKg = 100 };
        var reservation = new InventoryReservation { OrderDetail = line, Inventory = stock, ReservedQuantity = 100, PickedQuantity = 40, ReservationStatus = "PARTIALLY_PICKED", ReservedAt = DateTime.UtcNow };
        db.Add(reservation); await db.SaveChangesAsync();
        db.Add(new PickingHistory { InventoryReservationId = reservation.InventoryReservationId, PickedByAccountId = account.AccountId, PickedQuantity = 40, PickedAt = DateTime.UtcNow });
        db.Add(new GoodsIssue { IssueCode = "MIGRATION-DRAFT", PurchaseOrderId = order.PurchaseOrderId, OperationAccountId = account.AccountId,
            IssuedAt = DateTime.UtcNow.Date, IssueStatus = "DRAFT", GoodsIssueDetails = [new GoodsIssueDetail {
                OrderDetailId = line.OrderDetailId, InventoryReservationId = reservation.InventoryReservationId, InventoryId = stock.InventoryId, IssuedQuantity = 20, Unit = "kg", WeightInKg = 20 }] });
        await db.SaveChangesAsync();
    }
    private static async Task<string> BusinessSnapshotAsync(AppDbContext db)
    {
        var tables = new[] { "PRODUCT_BATCH", "PURCHASE_ORDER", "ORDER_DETAIL", "INVENTORY", "INVENTORY_RESERVATION", "PICKING_HISTORY", "GOODS_ISSUE", "GOODS_ISSUE_DETAIL", "BATCH_STATUS_HISTORY", "ORDER_STATUS_HISTORY", "AUDIT_LOG" };
        var result = new StringBuilder();
        await db.Database.OpenConnectionAsync();
        foreach (var table in tables)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT * FROM dbo.[{table}] ORDER BY 1 FOR JSON PATH,INCLUDE_NULL_VALUES";
            await using var reader = await command.ExecuteReaderAsync();
            result.Append(table); while (await reader.ReadAsync()) result.Append(reader.GetString(0));
        }
        return Digest(result.ToString());
    }
    [ReceivingSqlFact]
    public async Task Legacy_to_migration_repeat_down_and_up_preserve_data_and_restore_verified_definitions()
    {
        await using var fixture = new Database(); await fixture.CreateAsync(); await using var db = fixture.Context();
        await BootstrapLegacySchemaAsync(db); await SeedProbeAsync(db);
        var legacy = (await DefinitionsAsync(db)).Select(Canonical).Order().ToArray(); var before = await BusinessSnapshotAsync(db);
        var appliedBefore = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); Assert.Equal(Previous, appliedBefore.Last());
        await db.GetService<IMigrator>().MigrateAsync(Target);
        Assert.Equal(before, await BusinessSnapshotAsync(db));
        var newer = (await DefinitionsAsync(db)).Select(Canonical).Order().ToArray(); Assert.All(newer, value => Assert.Contains("SAW_GOODS_ISSUE_V1", value));
        Assert.Equal(appliedBefore.Length + 1, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await db.GetService<IMigrator>().MigrateAsync(Target); Assert.Equal(before, await BusinessSnapshotAsync(db));
        Assert.Equal(newer, (await DefinitionsAsync(db)).Select(Canonical).Order().ToArray());
        await db.GetService<IMigrator>().MigrateAsync(Previous); Assert.Equal(before, await BusinessSnapshotAsync(db));
        Assert.Equal(legacy, (await DefinitionsAsync(db)).Select(Canonical).Order().ToArray());
        await db.GetService<IMigrator>().MigrateAsync(Target); Assert.Equal(before, await BusinessSnapshotAsync(db));
        Assert.Equal(newer, (await DefinitionsAsync(db)).Select(Canonical).Order().ToArray());
    }
    [ReceivingSqlFact]
    public async Task Previously_applied_manual_trigger_SQL_is_adopted_by_EF_without_touching_business_rows()
    {
        await using var fixture = new Database(); await fixture.CreateAsync(); await using var db = fixture.Context();
        await BootstrapLegacySchemaAsync(db); await SeedProbeAsync(db); var before = await BusinessSnapshotAsync(db);
        // Equivalent to the previous standalone script: definition changes, no EF history writes.
        foreach (var operation in new FixGoodsIssueWorkflowTriggers().UpOperations.Skip(1).Cast<SqlOperation>()) await db.Database.ExecuteSqlRawAsync(operation.Sql);
        Assert.DoesNotContain(Target, await db.Database.GetAppliedMigrationsAsync());
        await db.GetService<IMigrator>().MigrateAsync(Target);
        Assert.Equal(before, await BusinessSnapshotAsync(db));
        Assert.Single(await db.Database.GetAppliedMigrationsAsync(), m => m == Target);
    }
    [ReceivingSqlFact]
    public async Task Unknown_member_trigger_changes_are_rejected_atomically_without_history_or_data_updates()
    {
        await using var fixture = new Database(); await fixture.CreateAsync(); await using var db = fixture.Context();
        await BootstrapLegacySchemaAsync(db); await SeedProbeAsync(db); var before = await BusinessSnapshotAsync(db);
        var unknown = ((SqlOperation)new FixGoodsIssueWorkflowTriggers().DownOperations[1]).Sql.Replace("SET NOCOUNT ON;", "SET NOCOUNT ON; -- LOCAL MEMBER CHANGE");
        await db.Database.ExecuteSqlRawAsync(unknown); var definitions = (await DefinitionsAsync(db)).ToArray();
        var exception = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(Target));
        Assert.Equal(50230, exception.Number); Assert.Equal(before, await BusinessSnapshotAsync(db));
        Assert.DoesNotContain(Target, await db.Database.GetAppliedMigrationsAsync()); Assert.Equal(definitions, await DefinitionsAsync(db));
    }
    [ReceivingSqlFact]
    public async Task Unreadable_encrypted_trigger_is_not_treated_as_missing_or_overwritten()
    {
        await using var fixture = new Database(); await fixture.CreateAsync(); await using var db = fixture.Context();
        await BootstrapLegacySchemaAsync(db); await SeedProbeAsync(db); var before = await BusinessSnapshotAsync(db);
        var encrypted = ((SqlOperation)new FixGoodsIssueWorkflowTriggers().DownOperations[1]).Sql
            .Replace("AFTER INSERT, UPDATE, DELETE", "WITH ENCRYPTION\nAFTER INSERT, UPDATE, DELETE");
        await db.Database.ExecuteSqlRawAsync(encrypted);
        var exception = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(Target));
        Assert.Equal(50230, exception.Number); Assert.Equal(before, await BusinessSnapshotAsync(db));
        Assert.DoesNotContain(Target, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.triggers WHERE name='TR_INVENTORY_RESERVATION_SYNC' AND OBJECT_DEFINITION(object_id) IS NULL").SingleAsync());
    }
    [ReceivingSqlFact]
    public async Task Empty_database_full_chain_records_existing_InitialCreate_bootstrap_limitation()
    {
        await using var fixture = new Database(); await fixture.CreateAsync(); await using var db = fixture.Context();
        Assert.Empty(new InitialCreate().UpOperations);
        var exception = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(Target));
        Assert.Contains("SUPPLIER", exception.Message);
        Assert.DoesNotContain(Target, await db.Database.GetAppliedMigrationsAsync());
        var evidence = Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrEmpty(evidence))
        {
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "empty-database-baseline-limitation.txt"),
                "Full EF chain on an empty database FAILED before the new migration. InitialCreate.Up is empty; AddGrowingArea requires the pre-existing legacy SUPPLIER table.\n" + exception.Message);
        }
    }
}
