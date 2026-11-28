using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiChat;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class PersistentAiChatSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task ClearDeletesOnlyOwnedTurnsAndRemainsDeletedAfterReload()
    {
        int otherId;
        await using (var db = fixture.Context())
        {
            var other = new Account
            {
                RoleId = await db.Accounts.Where(x => x.AccountId == fixture.ActorId).Select(x => x.RoleId).SingleAsync(),
                Username = "chat-" + Guid.NewGuid().ToString("N"),
                Email = Guid.NewGuid().ToString("N") + "@example.invalid",
                PasswordHash = "test", FullName = "Other chat owner", AccountStatus = "ACTIVE", CreatedAt = DateTime.UtcNow
            };
            db.Accounts.Add(other);
            await db.SaveChangesAsync();
            otherId = other.AccountId;
            db.AiChatTurns.AddRange(new AiChatTurn { AccountId = fixture.ActorId,
                    Message = "Câu hỏi của tôi", Reply = "Câu trả lời", SentAt = DateTime.UtcNow, RepliedAt = DateTime.UtcNow },
                new AiChatTurn { AccountId = otherId, Message = "Riêng tư", Reply = "Tin nhắn khác",
                    SentAt = DateTime.UtcNow, RepliedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await new PersistentAiChatService(null!, db).ClearHistoryAsync(fixture.ActorId, default);
        }

        await using var restored = fixture.Context();
        var history = new PersistentAiChatService(null!, restored);
        Assert.Empty((await history.GetHistoryAsync(fixture.ActorId, null, default)).Items);
        Assert.Equal("Riêng tư", Assert.Single((await history.GetHistoryAsync(otherId, null, default)).Items).Message);
        // Clearing an already empty history is safe.
        await history.ClearHistoryAsync(fixture.ActorId, default);
        Assert.Single((await history.GetHistoryAsync(otherId, null, default)).Items);
    }
}
