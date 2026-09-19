using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.CropTypes;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class CropTypeRepository(AppDbContext dbContext) : ICropTypeRepository
{
    public async Task<(IReadOnlyList<CropType> Items, int TotalCount)> SearchAsync(
        string? search, string? category, bool? isActive, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.CropTypes.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.CropCode.Contains(search) || x.CropName.Contains(search));
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.CategoryName == category);
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive.Value);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.CropName).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<CropType?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        dbContext.CropTypes.SingleOrDefaultAsync(x => x.CropTypeId == id, cancellationToken);
    public Task<bool> CodeExistsAsync(string code, int? excludingId, CancellationToken cancellationToken) =>
        dbContext.CropTypes.AnyAsync(x => x.CropCode == code && (!excludingId.HasValue || x.CropTypeId != excludingId), cancellationToken);
    public Task<bool> NameExistsAsync(string name, int? excludingId, CancellationToken cancellationToken) =>
        dbContext.CropTypes.AnyAsync(x => x.CropName == name && (!excludingId.HasValue || x.CropTypeId != excludingId), cancellationToken);
    public async Task<bool> IsInUseAsync(int id, CancellationToken cancellationToken) =>
        await dbContext.ProductBatches.AnyAsync(x => x.CropTypeId == id, cancellationToken)
        || await dbContext.OrderDetails.AnyAsync(x => x.CropTypeId == id, cancellationToken)
        || await dbContext.SupplierCropTypes.AnyAsync(x => x.CropTypeId == id, cancellationToken)
        || await dbContext.InspectionStandardSets.AnyAsync(x => x.CropTypeId == id, cancellationToken);
    public void Add(CropType cropType) => dbContext.CropTypes.Add(cropType);
    public void Remove(CropType cropType) => dbContext.CropTypes.Remove(cropType);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
