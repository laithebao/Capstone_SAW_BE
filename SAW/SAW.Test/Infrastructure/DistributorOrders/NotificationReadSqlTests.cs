using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.API.Controllers;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.DistributorOrders;

public sealed class NotificationReadSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task RetiredDraftNotifications_AreExcludedBeforePaginationAndFromUnreadCounts_WithoutDeletingHistory()
    {
        await using var db = fixture.Context();
        var key = Guid.NewGuid().ToString("N");
        var account = new Account { RoleId = (await db.Accounts.FindAsync(fixture.ActorId))!.RoleId,
            Username = key, Email = key + "@test.invalid", FullName = "Hidden notification test", PasswordHash = "test", AccountStatus = "ACTIVE" };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        var visible = new Notification { AccountId = account.AccountId, NotificationType = "GOODS_RECEIPT_COMMITTED",
            Title = "Confirmed", Message = "Confirmed", CreatedAt = DateTime.UtcNow };
        db.Notifications.Add(visible); await db.SaveChangesAsync();
        foreach (var type in new[] { "GOODS_RECEIPT_CREATED", "GOODS_ISSUE_CREATED" })
            db.Notifications.AddRange(Enumerable.Range(0, 25).Select(_ => new Notification { AccountId = account.AccountId,
                NotificationType = type, Title = "Old draft", Message = "Old draft", CreatedAt = DateTime.UtcNow }));
        await db.SaveChangesAsync();
        var controller = new NotificationsController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", account.AccountId.ToString())], "test")) } } };
        var list = Assert.IsType<ApiResponse<NotificationList>>(Assert.IsType<OkObjectResult>((await controller.List(default)).Result).Value).Data!;
        Assert.Equal(visible.NotificationId, Assert.Single(list.Items).Id);
        Assert.Equal(1, list.UnreadCount);
        var receipt = Assert.IsType<ApiResponse<NotificationReadReceipt>>(Assert.IsType<OkObjectResult>(await controller.MarkAllRead(default)).Value).Data!;
        Assert.Equal(0, receipt.UnreadCount);
        Assert.Equal(51, await db.Notifications.CountAsync(n => n.AccountId == account.AccountId));
        Assert.Equal(50, await db.Notifications.CountAsync(n => n.AccountId == account.AccountId && !n.IsRead));
        Assert.Equal(0, Assert.IsType<ApiResponse<NotificationList>>(Assert.IsType<OkObjectResult>((await controller.List(default)).Result).Value).Data!.UnreadCount);
    }

    [ReceivingSqlFact]
    public async Task OpeningBell_ClearsAllOwnUnread_PersistsAcrossReload_LeavesOtherUsersAndFutureEventsUnread()
    {
        await using var db = fixture.Context();
        var actor = await db.Accounts.FindAsync(fixture.ActorId);
        var key = Guid.NewGuid().ToString("N");
        var other = new Account { RoleId = actor!.RoleId, Username = key, Email = key + "@test.invalid", FullName = "Other user", PasswordHash = "test", AccountStatus = "ACTIVE" };
        db.Accounts.Add(other); await db.SaveChangesAsync();
        Notification Item(int owner, bool read = false) => new() { AccountId = owner, NotificationType = "TEST", Title = "Notification", Message = "Test",
            CreatedAt = DateTime.UtcNow, IsRead = read, ReadAt = read ? DateTime.UtcNow.AddDays(-1) : null };
        db.Notifications.AddRange(Enumerable.Range(0, 25).Select(_ => Item(fixture.ActorId)));
        var previouslyRead = Item(fixture.ActorId, true);
        var foreign = Item(other.AccountId);
        db.Notifications.AddRange(previouslyRead, foreign); await db.SaveChangesAsync();
        var previousReadAt = previouslyRead.ReadAt;
        var controller = new NotificationsController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", fixture.ActorId.ToString())], "test")) } } };
        var beforeOpen = Assert.IsType<ApiResponse<NotificationList>>(Assert.IsType<OkObjectResult>((await controller.List(default)).Result).Value).Data!;
        Assert.Equal(25, beforeOpen.UnreadCount); // Count is not limited to the 20 displayed items.
        Assert.Equal(20, beforeOpen.Items.Count);
        Assert.Equal(25, await db.Notifications.CountAsync(n => n.AccountId == fixture.ActorId && !n.IsRead)); // A GET never acknowledges events.
        var result = Assert.IsType<OkObjectResult>(await controller.MarkAllRead(default));
        var receipt = Assert.IsType<ApiResponse<NotificationReadReceipt>>(result.Value).Data!;
        Assert.Equal(0, receipt.UnreadCount);
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.AccountId == fixture.ActorId && !n.IsRead));
        Assert.False((await db.Notifications.FindAsync(foreign.NotificationId))!.IsRead);
        Assert.Equal(previousReadAt, (await db.Notifications.FindAsync(previouslyRead.NotificationId))!.ReadAt);
        Assert.True(await db.Notifications.Where(n => n.AccountId == fixture.ActorId).AllAsync(n => n.ReadAt != null));
        await controller.MarkAllRead(default); // Reopening does not change existing read timestamps.
        Assert.Equal(previousReadAt, (await db.Notifications.AsNoTracking().SingleAsync(n => n.NotificationId == previouslyRead.NotificationId)).ReadAt);
        var incoming = Item(fixture.ActorId); db.Notifications.Add(incoming); await db.SaveChangesAsync();
        Assert.True(incoming.NotificationId > receipt.ReadThroughId);
        var afterNewEvent = Assert.IsType<ApiResponse<NotificationList>>(Assert.IsType<OkObjectResult>((await controller.List(default)).Result).Value).Data!;
        Assert.Equal(1, afterNewEvent.UnreadCount);
        Assert.False(afterNewEvent.Items.Single(n => n.Id == incoming.NotificationId).IsRead);
        await using var reload = fixture.Context();
        Assert.Equal(1, await reload.Notifications.CountAsync(n => n.AccountId == fixture.ActorId && !n.IsRead));
    }
}
