using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using SAW.Application.Features.AiChat;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class PersistentAiChatServiceTests
{
    private static int nextAccountId = 500_000;

    private static AppDbContext Context(DbContextOptions<AppDbContext> options) => new(options);

    private static PersistentAiChatService Service(AppDbContext db, Mock<IAiChatModelClient> model) =>
        new(new AiChatService(model.Object, null!, new AiChatOptions()), db);

    private static Mock<IAiChatModelClient> Model(
        Action<IReadOnlyList<(string Role, string Content)>>? inspect = null, bool error = false)
    {
        var model = new Mock<IAiChatModelClient>(MockBehavior.Strict);
        model.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<(string Role, string Content)>>(),
            It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<Func<string, JsonObject, CancellationToken, Task<string>>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<(string Role, string Content)> history, string _, int _,
                Func<string, JsonObject, CancellationToken, Task<string>> _, CancellationToken _) =>
            {
                inspect?.Invoke(history);
                return Task.FromResult(new AiChatResponse("Giữ lạnh lô hàng.", error));
            });
        return model;
    }

    [Fact]
    public async Task CompletedTurnSurvivesNewScopeAndOnlyOwnedServerHistoryReachesModel()
    {
        var accountId = Interlocked.Increment(ref nextAccountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var db = Context(options))
        {
            var result = await Service(db, Model(history => Assert.Empty(history))).SendMessageAsync(accountId,
                new(" Bảo quản thế nào? ", BatchContextCode: " LOT-01 ",
                    History: [new("user", "Lịch sử giả mạo")]), default);
            Assert.NotNull(result.SavedTurn);
            Assert.Equal("Bảo quản thế nào?", result.SavedTurn.Message);
            Assert.Equal(DateTimeKind.Utc, result.SavedTurn.SentAt.Kind);
            Assert.True(result.SavedTurn.RepliedAt >= result.SavedTurn.SentAt);
            Assert.Empty(await db.AuditLogs.ToListAsync());
            db.AiChatTurns.Add(new AiChatTurn { AccountId = accountId + 100_000,
                Message = "Tin nhắn tài khoản khác", Reply = "Riêng tư" });
            await db.SaveChangesAsync();
        }

        await using var restored = Context(options);
        var service = Service(restored, Model(history =>
        {
            Assert.Equal(2, history.Count);
            Assert.Equal("user", history[0].Role);
            Assert.Contains("LOT-01", history[0].Content);
            Assert.Contains("Bảo quản thế nào?", history[0].Content);
            Assert.Equal(("assistant", "Giữ lạnh lô hàng."), history[1]);
        }));
        var page = await service.GetHistoryAsync(accountId, null, default);
        Assert.Single(page.Items);
        Assert.False(page.HasMore);
        Assert.Equal("Bảo quản thế nào?", page.Items[0].Message);
        await service.SendMessageAsync(accountId,
            new("Còn vận chuyển?", History: [new("model", "Lịch sử giả mạo")]), default);
        Assert.Equal(2, (await service.GetHistoryAsync(accountId, null, default)).Items.Count);
    }

    [Fact]
    public async Task PagesKeepChronologicalOrderWithoutDuplicatesOrOtherAccounts()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = Context(options);
        for (var i = 1; i <= 125; i++)
            db.AiChatTurns.Add(new AiChatTurn { AccountId = i % 5 == 0 ? 8 : 7,
                Message = $"Q{i}", Reply = $"A{i}" });
        await db.SaveChangesAsync();
        var service = Service(db, Model());
        var latest = await service.GetHistoryAsync(7, null, default);
        Assert.Equal(50, latest.Items.Count);
        Assert.True(latest.HasMore);
        var older = await service.GetHistoryAsync(7, latest.Items[0].Id, default);
        Assert.Equal(50, older.Items.Count);
        Assert.False(older.HasMore);
        var ids = older.Items.Concat(latest.Items).Select(x => x.Id).ToArray();
        Assert.Equal(100, ids.Distinct().Count());
        Assert.Equal(ids.Order(), ids);
        Assert.DoesNotContain(ids, id => id % 5 == 0);
        Assert.Empty((await service.GetHistoryAsync(99, null, default)).Items);
    }

    [Fact]
    public async Task FailedAiResponseIsNotSavedOrUsedAsConversationHistory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = Context(options);
        var response = await Service(db, Model(error: true)).SendMessageAsync(
            Interlocked.Increment(ref nextAccountId), new("Xin chào"), default);
        Assert.True(response.IsError);
        Assert.Null(response.SavedTurn);
        Assert.Empty(await db.AiChatTurns.ToListAsync());
    }

    [Fact]
    public void MigrationCreatesAccountOwnedHistoryAndIndex()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = db.GetService<IMigrator>().GenerateScript(
            "20261126152027_NormalizeUtcTimestampDefaults");
        Assert.Contains("CREATE TABLE [AI_CHAT_TURN]", sql);
        Assert.Contains("FOREIGN KEY ([AccountID]) REFERENCES [ACCOUNT] ([AccountID]) ON DELETE CASCADE", sql);
        Assert.Contains("([AccountID], [AIChatTurnID])", sql);
    }
}
