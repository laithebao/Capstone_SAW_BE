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
            await using var db = Database.Context();
            // Keep the real EF migration chain above. The legacy workflow definitions
            // are versioned test resources, so Database=master works on a clean server.
            await WarehouseWorkflowTestSchema.InstallAsync(db, "SAW.Test.Infrastructure.Picking.PickingSchema.sql", replaceExistingTriggers: true);
            await WarehouseWorkflowTestSchema.InstallAsync(db, "SAW.Test.Infrastructure.GoodsIssues.GoodsIssueSchema.sql", replaceExistingTriggers: true);
            await WarehouseWorkflowTestSchema.InstallAsync(db, "SAW.Test.Infrastructure.StockAdjustments.StockAdjustmentSchema.sql");
            var staffRole = await db.Roles.SingleOrDefaultAsync(r => r.RoleCode == "OPERATION_STAFF");
            if (staffRole == null) { staffRole = new SAW.Domain.Entities.Role { RoleCode = "OPERATION_STAFF", RoleName = "Fixture operation staff", IsActive = true }; db.Roles.Add(staffRole); await db.SaveChangesAsync(); }
            var staff = await db.Accounts.SingleAsync(a => a.AccountId == Database.ActorId); staff.RoleId = staffRole.RoleId; await db.SaveChangesAsync();
            Assert.Contains("20261016163023_AddStockAdjustmentRequestScope", await db.Database.GetAppliedMigrationsAsync());
        }
        catch { await Database.DisposeAsync(); throw; }
    }
    public Task DisposeAsync() => Database.DisposeAsync();
}
