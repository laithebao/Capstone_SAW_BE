using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.AdminDashboard;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class AdminDashboardRepository(AppDbContext dbContext) : IAdminDashboardRepository
{
    public async Task<AdminDashboardResponse> GetAsync(DateTime fromDate, CancellationToken cancellationToken)
    {
        var totalAccounts = await dbContext.Accounts.CountAsync(cancellationToken);
        var activeAccounts = await dbContext.Accounts.CountAsync(x => x.AccountStatus == "ACTIVE", cancellationToken);
        var pendingAccounts = await dbContext.Accounts.CountAsync(x => x.AccountStatus == "PENDING", cancellationToken);
        var totalCropTypes = await dbContext.CropTypes.CountAsync(cancellationToken);
        var activeCropTypes = await dbContext.CropTypes.CountAsync(x => x.IsActive, cancellationToken);
        var totalBatches = await dbContext.ProductBatches.CountAsync(cancellationToken);
        var pendingBatches = await dbContext.ProductBatches.CountAsync(x => x.BatchStatus.Contains("PENDING"), cancellationToken);
        var totalSuppliers = await dbContext.Suppliers.CountAsync(cancellationToken);
        var totalDistributors = await dbContext.Distributors.CountAsync(cancellationToken);

        // EF Core chỉ dịch projection kiểu đơn giản sang SQL. Tạo DTO record sau khi truy vấn xong.
        var roleCounts = await dbContext.Accounts
            .Join(dbContext.Roles,
                account => account.RoleId,
                role => role.RoleId,
                (_, role) => role.RoleName)
            .GroupBy(roleName => roleName)
            .Select(group => new { Label = group.Key, Value = group.Count() })
            .OrderByDescending(item => item.Value)
            .ToListAsync(cancellationToken);
        var distribution = roleCounts
            .Select(item => new DashboardCount(item.Label, item.Value))
            .ToList();

        var accountsByDay = await dbContext.Accounts.Where(x => x.CreatedAt >= fromDate)
            .GroupBy(x => x.CreatedAt.AddHours(7).Date).Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var batchesByDay = await dbContext.ProductBatches.Where(x => x.CreatedAt >= fromDate)
            .GroupBy(x => x.CreatedAt.AddHours(7).Date).Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var weekly = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = SAW.Domain.Common.VietnamTime.ToLocal(fromDate).Date.AddDays(offset);
            return new DailyDashboardActivity(date, accountsByDay.GetValueOrDefault(date), batchesByDay.GetValueOrDefault(date));
        }).ToList();

        // Audit timestamps are UTC; group days by the Vietnam local calendar (UTC+07).
        var firstLocalDay = DateTime.UtcNow.AddHours(7).Date.AddDays(-6);
        var firstUtc = firstLocalDay.AddHours(-7);
        var endUtc = firstLocalDay.AddDays(7).AddHours(-7);
        var auditCounts = await dbContext.AuditLogs.AsNoTracking()
            .Where(x => x.CreatedAt >= firstUtc && x.CreatedAt < endUtc)
            .GroupBy(x => x.CreatedAt.AddHours(7).Date)
            .Select(g => new
            {
                Day = g.Key,
                LoginSuccessCount = g.Count(x => x.ActionType == "LOGIN_SUCCESS"),
                LoginFailedCount = g.Count(x => x.ActionType == "LOGIN_FAILED"),
                OperationCount = g.Count(x => x.ActionType != "LOGIN_SUCCESS" && x.ActionType != "LOGIN_FAILED")
            }).ToDictionaryAsync(x => x.Day, cancellationToken);
        var auditActivity = Enumerable.Range(0, 7).Select(offset =>
        {
            var day = firstLocalDay.AddDays(offset);
            auditCounts.TryGetValue(day, out var count);
            return new DailyAuditActivity(DateOnly.FromDateTime(day), count?.LoginSuccessCount ?? 0,
                count?.LoginFailedCount ?? 0, count?.OperationCount ?? 0);
        }).ToList();

        return new AdminDashboardResponse(totalAccounts, activeAccounts, pendingAccounts, totalCropTypes, activeCropTypes,
            totalBatches, pendingBatches, totalSuppliers, totalDistributors, distribution, weekly, auditActivity);
    }
}
