using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Repositories;
using SAW.Infrastructure.QrCodes;

namespace SAW.Test.Infrastructure.QrCodes;

public sealed class QrCodeGenerationWorkerTests
{
    [Fact]
    public async Task FailedBatch_DoesNotPreventNextBatch_AndWorkerStopsOnCancellation()
    {
        var repo = new Mock<IQrCodeRepository>();
        repo.Setup(r => r.GetCandidatesAsync(0, 2, It.IsAny<CancellationToken>())).ReturnsAsync(new long[] { 1, 2 });
        var storage = new Mock<IQrImageStorage>();
        storage.SetupGet(s => s.IsConfigured).Returns(true);
        var service = new Mock<IProductBatchQrCodeService>();
        service.Setup(s => s.GenerateAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Setup(s => s.GenerateAsync(2, It.IsAny<CancellationToken>())).Returns(() => { processed.TrySetResult(); return Task.CompletedTask; });
        using var provider = new ServiceCollection().AddScoped(_ => repo.Object).AddScoped(_ => storage.Object)
            .AddScoped(_ => service.Object).BuildServiceProvider();
        using var worker = new QrCodeGenerationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new QrCodeOptions { PublicFrontendBaseUrl = "https://saw.example", BatchSize = 2 }, NullLogger<QrCodeGenerationWorker>.Instance);
        await worker.StartAsync(default);
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        service.Verify(s => s.GenerateAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.GenerateAsync(2, It.IsAny<CancellationToken>()), Times.Once);
    }
}
