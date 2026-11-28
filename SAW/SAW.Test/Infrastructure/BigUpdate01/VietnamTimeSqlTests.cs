using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Features.AuditLogs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.BigUpdate01;

public sealed class VietnamTimeSqlTests(ReceivingSqlFixture f) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task DateFiltersUseVietnamDayAndRealApiEmitsUtcSuffix()
    {
        await using var db = f.Context(); var marker = "TZ_" + Guid.NewGuid().ToString("N");
        var start = new DateTime(2026, 11, 26, 17, 0, 0, DateTimeKind.Utc);
        foreach (var time in new[] { start.AddSeconds(-1), start, start.AddDays(1).AddSeconds(-1), start.AddDays(1) })
            db.AuditLogs.Add(new AuditLog { ActionType = marker, EntityName = "TIMEZONE_TEST", CreatedAt = time });
        db.Notifications.Add(new Notification { AccountId = f.ActorId, NotificationType = "TIMEZONE_TEST", Title = marker, Message = "Isolated clock test", CreatedAt = start });
        await db.SaveChangesAsync();
        var filter = new AuditLogQuery(marker, new DateTime(2026, 11, 27), new DateTime(2026, 11, 27), null, null, null, null, null, false);
        var found = await new AuditLogRepository(db).SearchAsync(filter, true, default);
        Assert.Equal(2, found.TotalCount); Assert.All(found.Items, x => Assert.InRange(x.CreatedAt, start, start.AddDays(1).AddTicks(-1)));
        using var api = await f.ApiAsync();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "uc28-test", "uc28-test", [new("account_id", f.ActorId.ToString()), new(System.Security.Claims.ClaimTypes.Role, "ADMINISTRATOR")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256))));
        using var notifications = JsonDocument.Parse(await api.GetStringAsync("/api/notifications"));
        var timeText = notifications.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().Single(x => x.GetProperty("title").GetString() == marker).GetProperty("createdAt").GetString();
        Assert.Equal("2026-11-26T17:00:00Z", timeText);
        using var audit = JsonDocument.Parse(await api.GetStringAsync($"/api/admin/audit-logs?search={marker}&from=2026-11-27&to=2026-11-27"));
        Assert.Equal(2, audit.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
    }
}
