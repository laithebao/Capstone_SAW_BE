using SAW.Domain.Entities;
using SAW.Application.Features.InspectionStandards;

namespace SAW.Application.Repositories;

public interface IInspectionStandardRepository
{
    Task<bool> CropTypeExistsAsync(int id, CancellationToken token);
    Task<bool> CodeExistsAsync(string code, CancellationToken token);
    Task<IReadOnlyList<InspectionStandardListItem>> ListAsync(string? search, CancellationToken token);
    Task<InspectionStandardSet?> GetByIdAsync(int id, CancellationToken token);
    Task<int> GetMaxVersionNoAsync(int setId, CancellationToken token);
    void Add(InspectionStandardSet item);
    void AddAuditLog(AuditLog log);
    Task SaveChangesAsync(CancellationToken token);
}

