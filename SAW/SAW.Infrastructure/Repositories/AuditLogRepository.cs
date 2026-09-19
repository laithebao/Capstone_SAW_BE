using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.AuditLogs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class AuditLogRepository(AppDbContext dbContext) : IAuditLogRepository
{
    public async Task<(IReadOnlyList<AuditLog> Items, int TotalCount)> SearchAsync(
        AuditLogQuery filter, bool paginate, CancellationToken cancellationToken)
    {
        var query = dbContext.AuditLogs.Include(x => x.Account).ThenInclude(x => x!.Role).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            var idText = term.StartsWith("ALOG-", StringComparison.OrdinalIgnoreCase) ? term[5..] : term;
            var hasId = long.TryParse(idText, out var auditId);
            query = query.Where(x => (hasId && x.AuditLogId == auditId) || x.ActionType.Contains(term) || x.EntityName.Contains(term) ||
                (x.EntityId != null && x.EntityId.Contains(term)) ||
                (x.Description != null && x.Description.Contains(term)) ||
                (x.Account != null && (x.Account.FullName.Contains(term) || x.Account.Username.Contains(term))));
        }
        if (filter.From.HasValue) query = query.Where(x => x.CreatedAt >= filter.From.Value);
        if (filter.To.HasValue) query = query.Where(x => x.CreatedAt < filter.To.Value.Date.AddDays(1));
        if (filter.AccountId.HasValue) query = query.Where(x => x.AccountId == filter.AccountId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            var actor = filter.Actor.Trim();
            query = query.Where(x => x.Account != null &&
                (x.Account.FullName.Contains(actor) || x.Account.Username.Contains(actor) || x.Account.Email.Contains(actor)));
        }
        if (!string.IsNullOrWhiteSpace(filter.ActionType))
        {
            var action = filter.ActionType.Trim();
            query = query.Where(x => x.ActionType.Contains(action));
        }
        if (!string.IsNullOrWhiteSpace(filter.EntityName))
        {
            var entity = filter.EntityName.Trim();
            query = query.Where(x => x.EntityName.Contains(entity));
        }
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim().ToUpperInvariant();
            if (status == "FAILED") query = query.Where(x => x.ActionType.Contains("FAIL") || x.ActionType.Contains("ERROR"));
            else if (status == "WARNING") query = query.Where(x => x.ActionType.Contains("WARN"));
            else if (status == "SUCCESS") query = query.Where(x => !x.ActionType.Contains("FAIL") && !x.ActionType.Contains("ERROR") && !x.ActionType.Contains("WARN"));
        }
        if (filter.ChangesOnly)
            query = query.Where(x => (x.OldDataJson != null || x.NewDataJson != null) &&
                x.ActionType != "LOGIN_SUCCESS" && x.ActionType != "LOGIN_FAILED" &&
                x.ActionType != "PASSWORD_CHANGED");

        var total = await query.CountAsync(cancellationToken);
        query = query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.AuditLogId);
        if (paginate) query = query.Skip((Math.Max(1, filter.Page) - 1) * Math.Clamp(filter.PageSize, 1, 100)).Take(Math.Clamp(filter.PageSize, 1, 100));
        return (await query.ToListAsync(cancellationToken), total);
    }

    public Task<AuditLog?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
        dbContext.AuditLogs.Include(x => x.Account).ThenInclude(x => x!.Role)
            .AsNoTracking().SingleOrDefaultAsync(x => x.AuditLogId == id, cancellationToken);

}
