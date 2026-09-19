using SAW.Application.Exceptions;
using SAW.Domain.Entities;

namespace SAW.Application.Features.CropTypes;

public sealed class CropTypeService(ICropTypeRepository repository) : ICropTypeService
{
    public async Task<CropTypeListResponse> SearchAsync(string? search, string? category, bool? isActive, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await repository.SearchAsync(search?.Trim(), category?.Trim(), isActive, page, pageSize, cancellationToken);
        return new CropTypeListResponse(result.Items.Select(Map).ToList(), result.TotalCount, page, pageSize);
    }

    public async Task<CropTypeDto> GetAsync(int id, CancellationToken cancellationToken) =>
        Map(await FindAsync(id, cancellationToken));

    public async Task<CropTypeDto> CreateAsync(SaveCropTypeRequest request, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, null, cancellationToken);
        var entity = new CropType { CreatedAt = DateTime.UtcNow };
        Apply(entity, request);
        repository.Add(entity);
        await repository.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<CropTypeDto> UpdateAsync(int id, SaveCropTypeRequest request, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(id, cancellationToken);
        await ValidateAsync(request, id, cancellationToken);
        Apply(entity, request);
        entity.UpdatedAt = DateTime.UtcNow;
        await repository.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<CropTypeDto> SetStatusAsync(int id, bool isActive, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(id, cancellationToken);
        entity.IsActive = isActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await repository.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(id, cancellationToken);
        if (await repository.IsInUseAsync(id, cancellationToken))
            throw new ConflictException("Loại nông sản đang được sử dụng. Hãy chuyển sang trạng thái ngừng hoạt động thay vì xóa.");
        repository.Remove(entity);
        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task<CropType> FindAsync(int id, CancellationToken cancellationToken) =>
        await repository.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("Không tìm thấy loại nông sản.");

    private async Task ValidateAsync(SaveCropTypeRequest request, int? id, CancellationToken cancellationToken)
    {
        if (request.MinTemperature > request.MaxTemperature) throw new BadRequestException("Nhiệt độ thấp nhất không được lớn hơn nhiệt độ cao nhất.");
        if (request.MinHumidity > request.MaxHumidity) throw new BadRequestException("Độ ẩm thấp nhất không được lớn hơn độ ẩm cao nhất.");
        if (await repository.CodeExistsAsync(request.Code.Trim(), id, cancellationToken)) throw new ConflictException("Mã loại nông sản đã tồn tại.");
        if (await repository.NameExistsAsync(request.Name.Trim(), id, cancellationToken)) throw new ConflictException("Tên loại nông sản đã tồn tại.");
    }

    private static void Apply(CropType entity, SaveCropTypeRequest request)
    {
        entity.CropCode = request.Code.Trim().ToUpperInvariant();
        entity.CropName = request.Name.Trim();
        entity.CategoryName = request.CategoryName.Trim();
        entity.Description = string.IsNullOrWhiteSpace(request.ScientificName) ? null : request.ScientificName.Trim();
        entity.ExpectedMinTempC = request.MinTemperature;
        entity.ExpectedMaxTempC = request.MaxTemperature;
        entity.ExpectedMinHumidityPct = request.MinHumidity;
        entity.ExpectedMaxHumidityPct = request.MaxHumidity;
        entity.ShelfLifeDays = request.ShelfLifeDays;
        entity.SafetyStockLevelKg = request.SafetyStockLevelKg;
        entity.DefaultUnit = request.DefaultUnit.Trim();
        entity.IsActive = request.IsActive;
    }

    private static CropTypeDto Map(CropType entity) => new(
        entity.CropTypeId, entity.CropCode, entity.CropName, entity.CategoryName, entity.Description,
        entity.ExpectedMinTempC, entity.ExpectedMaxTempC, entity.ExpectedMinHumidityPct,
        entity.ExpectedMaxHumidityPct, entity.ShelfLifeDays, entity.SafetyStockLevelKg,
        entity.DefaultUnit, entity.IsActive, entity.CreatedAt, entity.UpdatedAt);
}
