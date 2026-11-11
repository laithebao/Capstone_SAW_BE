using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.API.Controllers;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.Notifications;

public sealed class NotificationReadTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static NotificationsController Controller(AppDbContext db, int? actor = 7) => new(db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(actor.HasValue ? new ClaimsIdentity([new Claim("account_id", actor.Value.ToString())], "Test") : new ClaimsIdentity())
        } }
    };
    private static Notification Item(long id, int account = 7, string type = "BATCH_STATUS") => new()
        { NotificationId = id, AccountId = account, NotificationType = type, Title = $"Message {id}", Message = "Body", CreatedAt = new DateTime(2026, 11, 1).AddSeconds(id) };

    [Fact]
    public async Task BellShowsOnlyOwnVisibleNotificationsWithStableCursorAndFullUnreadCount()
    {
        await using var db = Database();
        db.Notifications.AddRange(Enumerable.Range(1, 30).Select(id => Item(id)));
        db.Notifications.AddRange(Item(100, 8), Item(101, type: "GOODS_RECEIPT_CREATED"), Item(102, type: "GOODS_ISSUE_CREATED"));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var response = await Controller(db).List(default);
        var first = Assert.IsType<ApiResponse<NotificationList>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        Assert.Equal(30, first.UnreadCount); Assert.Equal(20, first.Items.Count);
        Assert.Equal(Enumerable.Range(11, 20).Reverse().Select(id => (long)id), first.Items.Select(item => item.Id));
        var olderResponse = await Controller(db).List(default, 11);
        var older = Assert.IsType<ApiResponse<NotificationList>>(Assert.IsType<OkObjectResult>(olderResponse.Result).Value).Data!;
        Assert.Equal(30, older.UnreadCount); Assert.Equal(Enumerable.Range(1, 10).Reverse().Select(id => (long)id), older.Items.Select(item => item.Id));
        Assert.All(await db.Notifications.AsNoTracking().ToListAsync(), item => Assert.False(item.IsRead));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadReceiptUpdatesOnlyOwnedNotificationAndRepeatedReadPreservesFirstReadTime()
    {
        await using var db = Database(); db.AddRange(Item(1), Item(2, 8)); await db.SaveChangesAsync();
        var controller = Controller(db);
        Assert.IsType<OkObjectResult>(await controller.MarkRead(1, default));
        var firstReadAt = (await db.Notifications.FindAsync(1L))!.ReadAt;
        Assert.NotNull(firstReadAt);
        Assert.IsType<OkObjectResult>(await controller.MarkRead(1, default));
        Assert.Equal(firstReadAt, (await db.Notifications.FindAsync(1L))!.ReadAt);
        Assert.False((await db.Notifications.FindAsync(2L))!.IsRead);
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ForeignOrRetiredDraftNotificationCannotBeMarkedRead(long id)
    {
        await using var db = Database(); db.AddRange(Item(2, 8), Item(3, type: "GOODS_RECEIPT_CREATED")); await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await Controller(db).MarkRead(id, default));
        Assert.All(await db.Notifications.ToListAsync(), item => { Assert.False(item.IsRead); Assert.Null(item.ReadAt); });
    }

    [Fact]
    public async Task MissingIdentityCannotReadListOrWriteReadReceipts()
    {
        await using var db = Database(); var controller = Controller(db, null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.List(default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.MarkRead(1, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.MarkAllRead(default));
        Assert.Empty(db.ChangeTracker.Entries());
    }
}
