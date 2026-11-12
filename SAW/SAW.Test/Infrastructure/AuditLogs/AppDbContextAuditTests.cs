using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.AuditLogs;

public sealed class AppDbContextAuditTests
{
    private static AppDbContext Database()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", "7")], "Test")) };
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.7");
        return new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new HttpContextAccessor { HttpContext = context });
    }
    private static async Task<Account> User(AppDbContext db)
    {
        var role = new Role { RoleId = 1, RoleCode = "QC_STAFF", RoleName = "QC" };
        var other = new Role { RoleId = 2, RoleCode = "ADMINISTRATOR", RoleName = "Admin" };
        var account = new Account { AccountId = 8, Role = role, Username = "user8", Email = "user8@test.invalid", FullName = "Before name", PasswordHash = "sensitive-old-hash", AccountStatus = "ACTIVE" };
        db.AddRange(account, other); await db.SaveChangesAsync(); return account;
    }

    [Fact]
    public async Task ProfileAuditContainsBeforeAfterAndActor_AndExcludesPasswordMaterial()
    {
        await using var db = Database(); var account = await User(db);
        account.FullName = "After name"; account.PhoneNumber = "0901234567"; await db.SaveChangesAsync();
        var log = Assert.Single(await db.AuditLogs.ToListAsync(), item => item.ActionType == "UPDATE");
        Assert.Equal(7, log.AccountId); Assert.Equal("127.0.0.7", log.IpAddress);
        Assert.Contains("Before name", log.OldDataJson); Assert.Contains("After name", log.NewDataJson);
        Assert.DoesNotContain("PasswordHash", log.NewDataJson!); Assert.DoesNotContain("sensitive-old-hash", log.OldDataJson!);
    }

    [Theory]
    [InlineData("login", "LOGIN_SUCCESS")]
    [InlineData("failedLogin", "LOGIN_FAILED")]
    [InlineData("password", "PASSWORD_CHANGED")]
    [InlineData("status", "ACCOUNT_STATUS_CHANGED")]
    [InlineData("role", "ACCOUNT_ROLE_CHANGED")]
    public async Task AccountChangesProduceTheirSemanticAuditActionWithoutLeakingCredentials(string change, string expected)
    {
        await using var db = Database(); var account = await User(db);
        switch (change)
        {
            case "login": account.LastLoginAt = DateTime.UtcNow; break;
            case "failedLogin": account.FailedLoginAttempts++; break;
            case "password": account.PasswordHash = "sensitive-new-hash"; account.UpdatedAt = DateTime.UtcNow; break;
            case "status": account.AccountStatus = "LOCKED"; break;
            case "role": account.RoleId = 2; break;
        }
        await db.SaveChangesAsync();
        var logs = await db.AuditLogs.ToListAsync(); Assert.Contains(logs, log => log.ActionType == expected && log.AccountId == 7);
        Assert.DoesNotContain(logs, log => (log.NewDataJson ?? "").Contains("sensitive-") || (log.OldDataJson ?? "").Contains("sensitive-"));
    }

    [Fact]
    public async Task UnchangedEntitiesAndReadReceiptsDoNotEmitDuplicateBusinessAudits()
    {
        await using var db = Database(); _ = await User(db); var before = await db.AuditLogs.CountAsync();
        await db.SaveChangesAsync(); Assert.Equal(before, await db.AuditLogs.CountAsync());
        db.Add(new Notification { AccountId = 8, NotificationType = "TEST", Title = "Title", Message = "Body" }); await db.SaveChangesAsync();
        var notification = await db.Notifications.SingleAsync(); notification.IsRead = true; notification.ReadAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); Assert.Equal(before, await db.AuditLogs.CountAsync());
    }
}
