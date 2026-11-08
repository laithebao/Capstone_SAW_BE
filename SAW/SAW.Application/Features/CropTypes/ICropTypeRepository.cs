using SAW.Domain.Entities;

namespace SAW.Application.Features.CropTypes;

public interface ICropTypeRepository
{
    Task<(IReadOnlyList<CropType> Items, int TotalCount)> SearchAsync(
        string? search, string? category, bool? isActive, int page, int pageSize, CancellationToken cancellationToken);
    Task<CropType?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task<bool> CodeExistsAsync(string code, int? excludingId, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(string name, int? excludingId, CancellationToken cancellationToken);
    Task<bool> IsInUseAsync(int id, CancellationToken cancellationToken);
    void Add(CropType cropType);
    void Remove(CropType cropType);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
