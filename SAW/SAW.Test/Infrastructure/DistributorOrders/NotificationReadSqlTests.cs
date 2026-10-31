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
        await using var reload = fixture.Context();
        Assert.Equal(1, await reload.Notifications.CountAsync(n => n.AccountId == fixture.ActorId && !n.IsRead));
    }
}
