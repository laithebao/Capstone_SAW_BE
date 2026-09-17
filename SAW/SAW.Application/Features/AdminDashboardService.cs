namespace SAW.Application.Features.AdminDashboard;

public sealed record DashboardCount(string Label, int Value);
public sealed record DailyDashboardActivity(DateTime Date, int AccountCount, int BatchCount);
public sealed record AdminDashboardResponse(
    int TotalAccounts, int ActiveAccounts, int PendingAccounts,
    int TotalCropTypes, int ActiveCropTypes,
    int TotalBatches, int PendingBatches,
    int TotalSuppliers, int TotalDistributors,
    IReadOnlyList<DashboardCount> RoleDistribution,
    IReadOnlyList<DailyDashboardActivity> WeeklyActivity);

public interface IAdminDashboardRepository
{
    Task<AdminDashboardResponse> GetAsync(DateTime fromDate, CancellationToken cancellationToken);
}

public interface IAdminDashboardService
{
    Task<AdminDashboardResponse> GetAsync(CancellationToken cancellationToken);
}

public sealed class AdminDashboardService(IAdminDashboardRepository repository) : IAdminDashboardService
{
    public Task<AdminDashboardResponse> GetAsync(CancellationToken cancellationToken) =>
        repository.GetAsync(DateTime.UtcNow.Date.AddDays(-6), cancellationToken);
}
