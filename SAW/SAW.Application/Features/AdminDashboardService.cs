namespace SAW.Application.Features.AdminDashboard;

public sealed record DashboardCount(string Label, int Value);
public sealed record DailyDashboardActivity(DateTime Date, int AccountCount, int BatchCount);
public sealed record DailyAuditActivity(DateOnly Date, int LoginSuccessCount, int LoginFailedCount, int OperationCount);
public sealed record AdminDashboardResponse(
    int TotalAccounts, int ActiveAccounts, int PendingAccounts,
    int TotalCropTypes, int ActiveCropTypes,
    int TotalBatches, int PendingBatches,
    int TotalSuppliers, int TotalDistributors,
    IReadOnlyList<DashboardCount> RoleDistribution,
    IReadOnlyList<DailyDashboardActivity> WeeklyActivity,
    IReadOnlyList<DailyAuditActivity>? AuditActivity = null);

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
        repository.GetAsync(SAW.Domain.Common.VietnamTime.StartOfDayUtc(SAW.Domain.Common.VietnamTime.Today.AddDays(-6)), cancellationToken);
}
