namespace SAW.Application.Features.CropTypes;

public interface ICropTypeService
{
    Task<CropTypeListResponse> SearchAsync(string? search, string? category, bool? isActive, int page, int pageSize, CancellationToken cancellationToken);
    Task<CropTypeDto> GetAsync(int id, CancellationToken cancellationToken);
    Task<CropTypeDto> CreateAsync(SaveCropTypeRequest request, CancellationToken cancellationToken);
    Task<CropTypeDto> UpdateAsync(int id, SaveCropTypeRequest request, CancellationToken cancellationToken);
    Task<CropTypeDto> SetStatusAsync(int id, bool isActive, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
}
