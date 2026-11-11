using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.AuditLogs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.AuditLogs;

public sealed class AuditLogRepositoryTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static AuditLogQuery Query(bool changes = false, string? status = null, int page = 1, int size = 20) =>
        new(null, null, null, null, null, null, "AUDIT_FIXTURE", status, changes, page, size);
    private static AuditLog Log(long id, string action, DateTime time, string? old = null, string? updated = null) => new()
        { AuditLogId = id, ActionType = action, EntityName = "AUDIT_FIXTURE", CreatedAt = time, OldDataJson = old, NewDataJson = updated };

    [Fact]
    public async Task HistoryOrdersByTimeAndId_AndPaginationDoesNotChangeTotalOrExportScope()
    {
        await using var db = Database(); var date = new DateTime(2026, 11, 1);
        db.AddRange(Log(101, "UPDATE", date), Log(102, "UPDATE", date), Log(103, "UPDATE", date.AddDays(-1)));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var repository = new AuditLogRepository(db);
        var page = await repository.SearchAsync(Query(page: 2, size: 1), true, default);
        Assert.Equal(3, page.TotalCount); Assert.Equal(101, Assert.Single(page.Items).AuditLogId);
        var export = await repository.SearchAsync(Query(page: 2, size: 1), false, default);
        Assert.Equal(new long[] { 102, 101, 103 }, export.Items.Select(item => item.AuditLogId));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ChangesViewExcludesAuthenticationAndPasswordEventsButKeepsBusinessBeforeAfterData()
    {
        await using var db = Database(); var date = new DateTime(2026, 11, 1);
        db.AddRange(Log(101, "LOGIN_SUCCESS", date, updated: "{}"), Log(102, "LOGIN_FAILED", date, updated: "{}"),
            Log(103, "PASSWORD_CHANGED", date, updated: "{}"), Log(104, "UPDATE", date, "{\"name\":\"old\"}", "{\"name\":\"new\"}"),
            Log(105, "UPDATE", date));
        await db.SaveChangesAsync();
        var result = await new AuditLogRepository(db).SearchAsync(Query(changes: true), false, default);
        Assert.Equal(104, Assert.Single(result.Items).AuditLogId);
    }

    [Theory]
    [InlineData("FAILED", "LOGIN_FAILED,PROVIDER_ERROR")]
    [InlineData("WARNING", "STOCK_WARN")]
    [InlineData("SUCCESS", "UPDATE")]
    public async Task StatusFilterMatchesFailureWarningAndSuccessEvents(string status, string expected)
    {
        await using var db = Database(); var date = new DateTime(2026, 11, 1);
        db.AddRange(Log(101, "LOGIN_FAILED", date), Log(102, "PROVIDER_ERROR", date), Log(103, "STOCK_WARN", date), Log(104, "UPDATE", date));
        await db.SaveChangesAsync();
        var result = await new AuditLogRepository(db).SearchAsync(Query(status: status), false, default);
        Assert.Equal(expected.Split(',').Order(), result.Items.Select(item => item.ActionType).Order());
    }

    [Fact]
    public async Task AuditDateRangeIncludesEntireEndDate_AndFormattedIdFindsExactRecord()
    {
        await using var db = Database(); var date = new DateTime(2026, 11, 1);
        db.AddRange(Log(101, "UPDATE", date), Log(102, "UPDATE", date.AddHours(23).AddMinutes(59)), Log(103, "UPDATE", date.AddDays(1)));
        await db.SaveChangesAsync(); var repository = new AuditLogRepository(db);
        var result = await repository.SearchAsync(Query() with { From = date, To = date }, true, default);
        Assert.Equal(new long[] { 102, 101 }, result.Items.Select(item => item.AuditLogId));
        var exact = await repository.SearchAsync(Query() with { Search = "ALOG-000102" }, true, default);
        Assert.Equal(102, Assert.Single(exact.Items).AuditLogId);
    }
}
