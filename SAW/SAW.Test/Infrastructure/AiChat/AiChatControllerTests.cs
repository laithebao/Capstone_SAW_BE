using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Features.AiChat;
using SAW.Domain.Common;
using SAW.Infrastructure.AiChat;
using SAW.Infrastructure.AiChat.Rag;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class AiChatControllerTests
{
    private readonly Mock<IHttpClientFactory> factory = new(MockBehavior.Strict);
    private AiChatController Controller(IAiChatService service, string? actor = "7", string claim = "account_id")
    {
        var options = new OpenAIOptions();
        var knowledge = new KnowledgeBaseService(new OpenAIEmbeddingService(factory.Object, options, NullLogger<OpenAIEmbeddingService>.Instance), [], new AiChatOptions(), options,
            new QdrantKnowledgeStore(factory.Object, new QdrantOptions()), NullLogger<KnowledgeBaseService>.Instance);
        return new(service, knowledge) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity(actor is null ? [] : [new Claim(claim, actor)], "Test")) } } };
    }

    [Theory]
    [InlineData(false, "account_id")]
    [InlineData(true, ClaimTypes.NameIdentifier)]
    public async Task UserFacingChatErrorRemainsHttpSuccessSoClientCanDisplayTheReply(bool isError, string claim)
    {
        var request = new AiChatRequest("Question", BatchContextId: 10); var response = new AiChatResponse("User-facing reply", isError);
        using var cancellation = new CancellationTokenSource(); var service = new Mock<IAiChatService>(MockBehavior.Strict);
        service.Setup(s => s.SendMessageAsync(7, request, cancellation.Token)).ReturnsAsync(response);
        var result = Assert.IsType<OkObjectResult>(await Controller(service.Object, claim: claim).Chat(request, cancellation.Token));
        var body = Assert.IsType<ApiResponse<AiChatResponse>>(result.Value); Assert.True(body.IsSuccess); Assert.Equal(response, body.Data); service.VerifyAll(); factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    public async Task ChatRequiresPositiveActorBeforeCallingAiService(string? actor)
    {
        var service = new Mock<IAiChatService>(MockBehavior.Strict);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Controller(service.Object, actor).Chat(new AiChatRequest("Question"), default)); service.VerifyNoOtherCalls(); factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnconfiguredKnowledgeIndexReportsUnavailableAndReindexReturns503WithoutNetworkCalls()
    {
        var controller = Controller(Mock.Of<IAiChatService>(MockBehavior.Strict));
        var status = Assert.IsType<OkObjectResult>(await controller.GetKbStatus(default));
        var body = Assert.IsType<ApiResponse<object>>(status.Value);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(body.Data);
        Assert.False(json.GetProperty("isReady").GetBoolean()); Assert.Equal(0, json.GetProperty("chunkCount").GetInt32()); Assert.Equal(0, json.GetProperty("indexedFiles").GetArrayLength());
        var reindex = Assert.IsType<ObjectResult>(await controller.ReindexKb(default)); Assert.Equal(503, reindex.StatusCode);
        Assert.False(Assert.IsType<ApiResponse<object>>(reindex.Value).IsSuccess); factory.VerifyNoOtherCalls();
    }
}
