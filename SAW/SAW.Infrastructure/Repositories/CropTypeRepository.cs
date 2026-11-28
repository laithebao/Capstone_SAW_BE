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
        var query = dbContext.CropTypes.Include(x => x.Category).ThenInclude(x => x!.Parent).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.CropCode.Contains(search) || x.CropName.Contains(search));
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.CategoryName == category);
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive.Value);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.CropName).ThenBy(x => x.CropTypeId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<(IReadOnlyList<CropType> Items, int TotalCount)> LookupAsync(
        string? search, int? categoryId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.CropTypes.Include(x => x.Category).ThenInclude(x => x!.Parent).AsNoTracking()
            .Where(x => x.IsActive && (x.CategoryId == null || x.Category!.IsActive
                && x.Category.ParentId != null && x.Category.Parent!.IsActive));
        if (categoryId.HasValue) query = query.Where(x => x.CategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.CropCode.Contains(search)
            || EF.Functions.Collate(x.CropName, "Latin1_General_100_CI_AI").Contains(search));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.CropName).ThenBy(x => x.CropTypeId)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<CropType?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        dbContext.CropTypes.Include(x => x.Category).ThenInclude(x => x!.Parent).SingleOrDefaultAsync(x => x.CropTypeId == id, cancellationToken);
    public Task<Category?> GetCategoryAsync(int id, CancellationToken cancellationToken) =>
        dbContext.Categories.Include(x => x.Parent).SingleOrDefaultAsync(x => x.CategoryId == id, cancellationToken);
    public Task<bool> CodeExistsAsync(string code, int? excludingId, CancellationToken cancellationToken) =>
        dbContext.CropTypes.AnyAsync(x => x.CropCode == code && (!excludingId.HasValue || x.CropTypeId != excludingId), cancellationToken);
    public Task<bool> NameExistsAsync(string name, int? excludingId, CancellationToken cancellationToken) =>
        dbContext.CropTypes.AnyAsync(x => x.CropName == name && (!excludingId.HasValue || x.CropTypeId != excludingId), cancellationToken);
    public async Task<bool> IsInUseAsync(int id, CancellationToken cancellationToken) =>
        await dbContext.ProductBatches.AnyAsync(x => x.CropTypeId == id, cancellationToken)
        || await dbContext.OrderDetails.AnyAsync(x => x.CropTypeId == id, cancellationToken)
        || await dbContext.SupplierCropTypes.AnyAsync(x => x.CropTypeId == id, cancellationToken)
        || await dbContext.InspectionStandardSets.AnyAsync(x => x.CropTypeId == id, cancellationToken)
        || await dbContext.SupplyContracts.AnyAsync(x => x.CropTypeId == id, cancellationToken);
    public void Add(CropType cropType) => dbContext.CropTypes.Add(cropType);
    public void Remove(CropType cropType) => dbContext.CropTypes.Remove(cropType);
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        async Task SaveLocked()
        {
            // Use the same lock order as category edits. Re-read the authoritative
            // parent inside the transaction so a concurrent rename cannot leave a
            // newly saved crop with a stale CategoryName compatibility value.
            var categories = await dbContext.Categories.FromSqlRaw("SELECT * FROM CATEGORY WITH (UPDLOCK,HOLDLOCK)").AsNoTracking().ToListAsync(cancellationToken);
            foreach (var entry in dbContext.ChangeTracker.Entries<CropType>().Where(e => e.State is EntityState.Added or EntityState.Modified))
            {
                if (entry.Entity.CategoryId is not int categoryId) continue;
                var child = categories.SingleOrDefault(x => x.CategoryId == categoryId);
                var parent = categories.SingleOrDefault(x => x.CategoryId == child?.ParentId);
                if (child is null || parent is null || parent.ParentId is not null) throw new SAW.Application.Exceptions.BadRequestException("Danh mục con không còn hợp lệ.");
                var newSelection = entry.State == EntityState.Added || entry.Property(x => x.CategoryId).OriginalValue != categoryId;
                if (newSelection && (!child.IsActive || !parent.IsActive)) throw new SAW.Application.Exceptions.BadRequestException("Danh mục đã ngừng hoạt động.");
                entry.Entity.CategoryName = parent.Name;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        try
        {
            if (dbContext.Database.CurrentTransaction is not null) { await SaveLocked(); return; }
            await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
                await SaveLocked(); await tx.CommitAsync(cancellationToken);
            });
        }
        catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        { throw new SAW.Application.Exceptions.ConflictException("Mã hoặc tên nông sản đã tồn tại. Vui lòng chọn giá trị khác."); }
    }
}
