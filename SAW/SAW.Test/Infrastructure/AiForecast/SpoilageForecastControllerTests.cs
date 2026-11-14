using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Features.SpoilageForecast;
using SAW.Domain.Common;

namespace SAW.Test.Infrastructure.AiForecast;

public sealed class SpoilageForecastControllerTests
{
    private static SpoilageForecastController Controller(ISpoilageForecastService service, string? actor = "7") => new(service)
    { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(actor is null ? [] : [new Claim("account_id", actor)], "Test")) } } };

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(21, 20)]
    public async Task HistoryRequestClampsCountBeforeCallingService(int requested, int expected)
    {
        var service = new Mock<ISpoilageForecastService>(MockBehavior.Strict);
        service.Setup(s => s.GetForecastHistoryAsync(10, expected, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Assert.IsType<OkObjectResult>(await Controller(service.Object).GetForecastHistory(10, requested)); service.VerifyAll();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10, 10)]
    [InlineData(100, 50)]
    public async Task HighRiskRequestClampsLimitBeforeCallingService(int requested, int expected)
    {
        var service = new Mock<ISpoilageForecastService>(MockBehavior.Strict);
        service.Setup(s => s.GetHighRiskBatchesAsync(expected, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Assert.IsType<OkObjectResult>(await Controller(service.Object).GetHighRiskBatches(requested)); service.VerifyAll();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task InvalidBatchDoesNotQueryHistoryOrLatest(long id)
    {
        var service = new Mock<ISpoilageForecastService>(MockBehavior.Strict); var controller = Controller(service.Object);
        Assert.IsType<BadRequestObjectResult>(await controller.GetLatestForecast(id, default));
        Assert.IsType<BadRequestObjectResult>(await controller.GetForecastHistory(id)); service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingLatestForecastReturnsNotFound()
    {
        var service = new Mock<ISpoilageForecastService>(MockBehavior.Strict);
        service.Setup(s => s.GetLatestForecastAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync((SpoilageForecastResponse?)null);
        var response = Assert.IsType<NotFoundObjectResult>(await Controller(service.Object).GetLatestForecast(10, default));
        Assert.Equal(404, Assert.IsType<ApiResponse<object>>(response.Value).StatusCode);
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData("Provider unavailable", 502)]
    public async Task GenerationMapsProviderFailureToBadGatewayAndEmptySuccessToOk(string? error, int expected)
    {
        var service = new Mock<ISpoilageForecastService>(MockBehavior.Strict);
        service.Setup(s => s.GenerateForecastsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Array.Empty<SpoilageForecastResponse>(), error));
        var response = Assert.IsAssignableFrom<ObjectResult>(await Controller(service.Object).GenerateForecasts(default)); Assert.Equal(expected, response.StatusCode);
        if (error is not null) { var body = Assert.IsType<ApiResponse<object>>(response.Value); Assert.False(body.IsSuccess); Assert.Equal(error, body.Message); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("invalid")]
    public async Task MissingIdentityCannotTriggerModelGeneration(string? actor)
    {
        var service = new Mock<ISpoilageForecastService>(MockBehavior.Strict);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Controller(service.Object, actor).GenerateForecasts(default)); service.VerifyNoOtherCalls();
    }
}
