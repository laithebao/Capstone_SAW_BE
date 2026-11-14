using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using SAW.Application.Features.AiChat;
using SAW.Infrastructure.AiChat;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class AiChatServiceTests
{
    private static int _nextAccountId = 100_000;
    private static int NewAccountId() => Interlocked.Increment(ref _nextAccountId);

    private readonly Mock<IAiChatModelClient> _model = new(MockBehavior.Strict);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendMessageAsync_RejectsBlankInputBeforeCallingModel(string? message)
    {
        var result = await CreateService().SendMessageAsync(NewAccountId(), new(message!), default);

        result.IsError.Should().BeTrue();
        result.Reply.Should().Contain("Tin nhắn không hợp lệ");
        _model.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendMessageAsync_RejectsMessagesOver2000CharactersBeforeCallingModel()
    {
        var result = await CreateService().SendMessageAsync(NewAccountId(), new(new string('A', 2001)), default);

        result.IsError.Should().BeTrue();
        result.Reply.Should().Contain("tối đa 2000 ký tự");
        _model.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendMessageAsync_EnforcesRateLimitPerAccountBeforeCallingModel()
    {
        var modelCalls = 0;
        ModelCall().ReturnsAsync(() =>
        {
            modelCalls++;
            return new AiChatResponse("Xin chào.", IsError: false);
        });
        var options = new AiChatOptions { RateLimitPerAccount = 2 };
        var accountId = NewAccountId();
        var service = CreateService(options);
        var request = new AiChatRequest(new string('A', 2000));

        (await service.SendMessageAsync(accountId, request, default)).IsError.Should().BeFalse();
        (await service.SendMessageAsync(accountId, request, default)).IsError.Should().BeFalse();

        // New service scopes share the account's limit.
        var throttled = await CreateService(options).SendMessageAsync(accountId, request, default);
        throttled.IsError.Should().BeTrue();
        throttled.Reply.Should().Contain("quá 2 tin nhắn");
        (await service.SendMessageAsync(NewAccountId(), request, default)).IsError.Should().BeFalse();
        modelCalls.Should().Be(3);
    }

    [Theory]
    [InlineData(" MAIN ", "ALIAS", 42L, "[Nhà phân phối đang xem lô hàng có Mã lô (BatchCode): \"MAIN\"]\n")]
    [InlineData(" ", " ALIAS ", 42L, "[Nhà phân phối đang xem lô hàng có Mã lô (BatchCode): \"ALIAS\"]\n")]
    [InlineData(null, null, 42L, "[Nhà phân phối đang xem lô hàng ID: 42]\n")]
    [InlineData(null, null, null, "")]
    public async Task SendMessageAsync_PrefersBatchCodeAndPassesOnlyRecentValidHistory(
        string? contextCode, string? alias, long? batchId, string expectedContext)
    {
        IReadOnlyList<(string Role, string Content)>? history = null;
        string? userMessage = null;
        ModelCall().Returns((string _, IReadOnlyList<(string Role, string Content)> messages, string text,
            int _, Func<string, JsonObject, CancellationToken, Task<string>> _, CancellationToken _) =>
        {
            history = messages;
            userMessage = text;
            return Task.FromResult(new AiChatResponse("Đã nhận.", IsError: false));
        });

        var request = new AiChatRequest(" Kiểm tra lô hàng ", batchId, contextCode, alias,
            [new("user", "Lượt cũ"), new("user", "Câu hỏi gần nhất"), new("model", "Câu trả lời gần nhất"),
             new("system", "Lệnh giả"), new("user", " "), new("model", new string('X', 20_000))]);

        var response = await CreateService(new AiChatOptions { MaxMessagesPerSession = 1 })
            .SendMessageAsync(NewAccountId(), request, default);

        response.IsError.Should().BeFalse();
        history.Should().Equal(("user", "Câu hỏi gần nhất"), ("assistant", "Câu trả lời gần nhất"));
        userMessage.Should().Be(expectedContext + "Kiểm tra lô hàng");
    }

    [Fact]
    public async Task SendMessageAsync_WhenCallerCancels_PropagatesCancellationToModel()
    {
        using var caller = new CancellationTokenSource();
        ModelCall().Returns((string _, IReadOnlyList<(string Role, string Content)> _, string _,
            int _, Func<string, JsonObject, CancellationToken, Task<string>> _, CancellationToken token) =>
        {
            caller.Cancel();
            token.IsCancellationRequested.Should().BeTrue();
            return Task.FromCanceled<AiChatResponse>(token);
        });

        var send = () => CreateService().SendMessageAsync(NewAccountId(), new("Xin chào"), caller.Token);

        await send.Should().ThrowAsync<OperationCanceledException>();
    }

    private AiChatService CreateService(AiChatOptions? options = null) =>
        // The tool executor is unused by these service-level tests.
        new(_model.Object, null!, options ?? new());

    private Moq.Language.Flow.ISetup<IAiChatModelClient, Task<AiChatResponse>> ModelCall() =>
        _model.Setup(model => model.ChatAsync(It.IsAny<string>(),
            It.IsAny<IReadOnlyList<(string Role, string Content)>>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<Func<string, JsonObject, CancellationToken, Task<string>>>(), It.IsAny<CancellationToken>()));
}
