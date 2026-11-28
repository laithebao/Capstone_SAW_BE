using Moq;
using SAW.Application.Features.AdminDashboard;

namespace SAW.Test.Application.AdminDashboard;

public sealed class AdminDashboardServiceTests
{
    [Fact]
    public async Task Dashboard_UsesSevenVietnamCalendarDaysWithUtcBoundary_AndPreservesRepositoryCounts()
    {
        var repository = new Mock<IAdminDashboardRepository>();
        using var cancellation = new CancellationTokenSource();
        var expected = new AdminDashboardResponse(12, 9, 3, 5, 4, 20, 2, 3, 4,
            [new("QC_STAFF", 2)], [new(DateTime.UtcNow.Date, 1, 2)]);
        DateTime? from = null;
        repository.Setup(x => x.GetAsync(It.IsAny<DateTime>(), cancellation.Token))
            .Callback<DateTime, CancellationToken>((date, _) => from = date).ReturnsAsync(expected);
        var earliest = SAW.Domain.Common.VietnamTime.StartOfDayUtc(SAW.Domain.Common.VietnamTime.Today.AddDays(-6));
        var result = await new AdminDashboardService(repository.Object).GetAsync(cancellation.Token);
        Assert.Same(expected, result);
        Assert.InRange(from!.Value, earliest, SAW.Domain.Common.VietnamTime.StartOfDayUtc(SAW.Domain.Common.VietnamTime.Today.AddDays(-6)));
        Assert.Equal(TimeSpan.FromHours(17), from.Value.TimeOfDay);
        repository.Verify(x => x.GetAsync(It.IsAny<DateTime>(), cancellation.Token), Times.Once);
    }
}
