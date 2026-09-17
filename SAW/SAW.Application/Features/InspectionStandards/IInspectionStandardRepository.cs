using SAW.Domain.Entities;
namespace SAW.Application.Features.InspectionStandards;
public interface IInspectionStandardRepository { Task<bool> CropTypeExistsAsync(int id, CancellationToken token); Task<bool> CodeExistsAsync(string code, CancellationToken token); Task<IReadOnlyList<InspectionStandardListItem>> ListAsync(string? search, CancellationToken token); void Add(InspectionStandardSet item); Task SaveChangesAsync(CancellationToken token); }
