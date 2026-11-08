using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Repositories;

namespace SAW.Infrastructure.QrCodes;

public sealed class QrCodeGenerationWorker(
    IServiceScopeFactory scopes, QrCodeOptions options, ILogger<QrCodeGenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using (var scope = scopes.CreateScope())
        {
            if (!options.HasValidPublicUrl || !scope.ServiceProvider.GetRequiredService<IQrImageStorage>().IsConfigured)
            {
                logger.LogWarning("UC55 worker paused: configure QrCode:PublicFrontendBaseUrl and Cloudinary backend credentials, then restart");
                return;
            }
        }
        long cursor = 0;
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 5, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<long> ids;
                using (var scope = scopes.CreateScope())
                    ids = await scope.ServiceProvider.GetRequiredService<IQrCodeRepository>()
                        .GetCandidatesAsync(cursor, options.BatchSize, stoppingToken);
                if (ids.Count == 0) cursor = 0;
                foreach (var id in ids)
                {
                    // Advance even on failure so one broken upload/batch never starves later IDs.
                    cursor = id;
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<IProductBatchQrCodeService>().GenerateAsync(id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogWarning("UC55 batch {BatchId} failed ({ErrorType}); retry on a later sweep", id, ex.GetType().Name); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("UC55 sweep failed ({ErrorType}); waiting before retry", ex.GetType().Name); }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
