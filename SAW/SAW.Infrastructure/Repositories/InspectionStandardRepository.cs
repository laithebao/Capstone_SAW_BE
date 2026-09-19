using Microsoft.EntityFrameworkCore;
using SAW.Application.Repositories;
using SAW.Application.Features.InspectionStandards;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class InspectionStandardRepository(AppDbContext dbContext) : IInspectionStandardRepository
{
    public Task<bool> CropTypeExistsAsync(int id, CancellationToken token)
        => dbContext.CropTypes.AnyAsync(x => x.CropTypeId == id && x.IsActive, token);

    public Task<bool> CodeExistsAsync(string code, CancellationToken token)
        => dbContext.InspectionStandardSets.AnyAsync(x => x.StandardCode == code, token);

    public async Task<IReadOnlyList<InspectionStandardListItem>> ListAsync(string? search, CancellationToken token)
    {
        var query = dbContext.InspectionStandardSets
            .AsNoTracking()
            .Include(x => x.CropType)
            .Include(x => x.Versions).ThenInclude(x => x.Criteria)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.StandardCode.Contains(search) || x.StandardName.Contains(search));

        var sets = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(token);

        return sets.Select(x =>
        {
            var version = x.Versions.OrderByDescending(v => v.VersionNo).FirstOrDefault();
            return new InspectionStandardListItem(
                x.InspectionStandardSetId,
                x.StandardCode,
                x.StandardName,
                x.CropType.CropName,
                version?.VersionNo ?? 0,
                version?.VersionStatus ?? "DRAFT",
                version?.EffectiveFrom,
                version?.Criteria.Count ?? 0);
        }).ToList();
    }

    public Task<InspectionStandardSet?> GetByIdAsync(int id, CancellationToken token)
        => dbContext.InspectionStandardSets
            .Include(x => x.CropType)
            .Include(x => x.Versions)
                .ThenInclude(v => v.Criteria)
                    .ThenInclude(c => c.GradeRules)
            .SingleOrDefaultAsync(x => x.InspectionStandardSetId == id, token);

    public Task<int> GetMaxVersionNoAsync(int setId, CancellationToken token)
        => dbContext.InspectionStandardVersions
            .Where(v => v.InspectionStandardSetId == setId)
            .Select(v => (int?)v.VersionNo)
            .MaxAsync(token)
            .ContinueWith(t => t.Result ?? 0, token);

    public void Add(InspectionStandardSet item) => dbContext.InspectionStandardSets.Add(item);

    public void AddAuditLog(AuditLog log) => dbContext.AuditLogs.Add(log);

    public Task SaveChangesAsync(CancellationToken token) => dbContext.SaveChangesAsync(token);
}
