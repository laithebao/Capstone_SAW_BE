using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Application.Features.SpoilageForecast;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiForecast;
using SAW.Test.Infrastructure.ProductBatches;
using Xunit;

namespace SAW.Test.Infrastructure.AiForecast;

public sealed class ForecastCacheSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task GenerateForecast_RequiresEligibleBatchBeforeServingFreshCache()
    {
        var batch = await fixture.BatchAsync();
        await using var db = fixture.Context();
        db.AiRiskForecasts.Add(new AiRiskForecast
        {
            ProductBatchId = batch.ProductBatchId,
            SpoilageRiskScore = 40m,
            RiskLevel = "MEDIUM",
            GeneratedAt = DateTime.UtcNow,
            ModelVersion = "cached-test-model"
        });
        await db.SaveChangesAsync();

        var model = new Mock<IForecastModelClient>(MockBehavior.Strict);
        var service = new SpoilageForecastService(db, model.Object, new ForecastInputCollector(db),
            NullLogger<SpoilageForecastService>.Instance);
        var request = new SpoilageForecastRequest(batch.ProductBatchId);

        var ineligible = await service.GenerateForecastAsync(fixture.ActorId, request);
        ineligible.IsError.Should().BeTrue();
        ineligible.ErrorMessage.Should().Contain("chưa hoàn tất nhập kho");

        await db.ProductBatches.Where(x => x.ProductBatchId == batch.ProductBatchId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.BatchStatus, "IN_STOCK"));

        var eligible = await service.GenerateForecastAsync(fixture.ActorId, request);
        eligible.IsError.Should().BeFalse();
        eligible.IsFromCache.Should().BeTrue();
        eligible.ModelVersion.Should().Be("cached-test-model");
        model.VerifyNoOtherCalls();
    }
}
