using SAW.Application.Exceptions;
using SAW.Domain.Entities;
using SAW.Domain.Common;

namespace SAW.Application.Features.CropTypes;

public sealed class CropTypeService(ICropTypeRepository repository) : ICropTypeService
{
    private static SaveCropTypeRequest Basic(SaveCropTypeBasicsRequest request, CropType? old = null) =>
        new(request.Code, request.Name, old?.CategoryName ?? "Danh mục", request.CategoryId,
            old?.ExpectedMinTempC, old?.ExpectedMaxTempC, old?.ExpectedMinHumidityPct, old?.ExpectedMaxHumidityPct,
            old?.ShelfLifeDays, old?.SafetyStockLevelKg, old?.DefaultUnit ?? "kg", request.IsActive);

    public async Task ValidateBasicCreateAsync(SaveCropTypeBasicsRequest request, CancellationToken cancellationToken) =>
        await ValidateAsync(Basic(request), null, cancellationToken);

    public Task<CropTypeDto> CreateBasicAsync(SaveCropTypeBasicsRequest request, CancellationToken cancellationToken) =>
        CreateAsync(Basic(request), cancellationToken);

    public async Task<CropTypeDto> UpdateBasicAsync(int id, SaveCropTypeBasicsRequest request, CancellationToken cancellationToken) =>
        await UpdateAsync(id, Basic(request, await FindAsync(id, cancellationToken)), cancellationToken);

    public async Task<CropTypeListResponse> SearchAsync(string? search, string? category, bool? isActive, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await repository.SearchAsync(search?.Trim(), category?.Trim(), isActive, page, pageSize, cancellationToken);
        return new CropTypeListResponse(result.Items.Select(Map).ToList(), result.TotalCount, page, pageSize);
    }

    public async Task<CropTypeDto> GetAsync(int id, CancellationToken cancellationToken) =>
        Map(await FindAsync(id, cancellationToken));

    public async Task<CropTypeListResponse> LookupAsync(string? search, int? categoryId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (categoryId is <= 0) throw new BadRequestException("Danh mục con không hợp lệ.");
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await repository.LookupAsync(search?.Trim(), categoryId, page, pageSize, cancellationToken);
        return new(result.Items.Select(Map).ToList(), result.TotalCount, page, pageSize);
    }

    public async Task<CropTypeDto> CreateAsync(SaveCropTypeRequest request, CancellationToken cancellationToken)
    {
        var category = await ValidateAsync(request, null, cancellationToken);
        var entity = new CropType { CreatedAt = DateTime.UtcNow };
        Apply(entity, request, category);
        repository.Add(entity);
        await repository.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<CropTypeDto> UpdateAsync(int id, SaveCropTypeRequest request, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(id, cancellationToken);
        var category = await ValidateAsync(request, id, cancellationToken, entity);
        Apply(entity, request, category);
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

    private async Task<Category?> ValidateAsync(SaveCropTypeRequest request, int? id, CancellationToken cancellationToken, CropType? existing = null)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 50 ||
            request.Code.Trim() != existing?.CropCode && !System.Text.RegularExpressions.Regex.IsMatch(request.Code.Trim().ToUpperInvariant(), "^[A-Z0-9_]+$"))
            throw new BadRequestException("Mã chỉ gồm chữ không dấu, số, dấu gạch dưới và tối đa 50 ký tự.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 150) throw new BadRequestException("Tên loại nông sản bắt buộc, tối đa 150 ký tự.");
        if (request.MinTemperature > request.MaxTemperature) throw new BadRequestException("Nhiệt độ thấp nhất không được lớn hơn nhiệt độ cao nhất.");
        if (request.MinHumidity > request.MaxHumidity) throw new BadRequestException("Độ ẩm thấp nhất không được lớn hơn độ ẩm cao nhất.");
        if (await repository.CodeExistsAsync(request.Code.Trim(), id, cancellationToken)) throw new ConflictException("Mã loại nông sản đã tồn tại.");
        if (await repository.NameExistsAsync(DisplayName.CapitalizeFirst(request.Name), id, cancellationToken)) throw new ConflictException("Tên loại nông sản đã tồn tại.");
        var category = request.CategoryId.HasValue ? await repository.GetCategoryAsync(request.CategoryId.Value, cancellationToken) : null;
        if (category is null || category.ParentId is null || category.Parent is null)
        {
            if (existing is not null && existing.CategoryId is null && request.CategoryId is null) return null;
            throw new BadRequestException("Vui lòng chọn danh mục con hợp lệ.");
        }
        if ((!category.IsActive || !category.Parent.IsActive) && existing?.CategoryId != category.CategoryId)
            throw new BadRequestException("Danh mục đã ngừng hoạt động.");
        return category;
    }

    private static void Apply(CropType entity, SaveCropTypeRequest request, Category? category)
    {
        entity.CropCode = request.Code.Trim().ToUpperInvariant();
        entity.CropName = DisplayName.CapitalizeFirst(request.Name);
        if (category is not null) { entity.CategoryId = category.CategoryId; entity.Category = category; entity.CategoryName = category.Parent!.Name; }
        entity.ExpectedMinTempC = MeasurementRounding.Round(request.MinTemperature);
        entity.ExpectedMaxTempC = MeasurementRounding.Round(request.MaxTemperature);
        entity.ExpectedMinHumidityPct = MeasurementRounding.Round(request.MinHumidity);
        entity.ExpectedMaxHumidityPct = MeasurementRounding.Round(request.MaxHumidity);
        entity.ShelfLifeDays = request.ShelfLifeDays;
        entity.SafetyStockLevelKg = request.SafetyStockLevelKg;
        entity.DefaultUnit = request.DefaultUnit.Trim();
        entity.IsActive = request.IsActive;
    }

    private static CropTypeDto Map(CropType entity) => new(
        entity.CropTypeId, entity.CropCode, entity.CropName, entity.CategoryName, entity.CategoryId,
        entity.ExpectedMinTempC, entity.ExpectedMaxTempC, entity.ExpectedMinHumidityPct,
        entity.ExpectedMaxHumidityPct, entity.ShelfLifeDays, entity.SafetyStockLevelKg,
        entity.DefaultUnit, entity.IsActive, entity.CreatedAt, entity.UpdatedAt, entity.Category?.ParentId, entity.Category?.Name);
}
