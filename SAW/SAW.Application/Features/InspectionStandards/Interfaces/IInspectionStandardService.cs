namespace SAW.Application.Features.InspectionStandards;

public interface IInspectionStandardService
{
    Task<IReadOnlyList<InspectionStandardListItem>> ListAsync(string? search, CancellationToken token);
    Task<InspectionStandardDetailDto> GetByIdAsync(int id, CancellationToken token);
    Task<InspectionStandardDto> CreateAsync(CreateInspectionStandardRequest request, CancellationToken token);
    Task<InspectionStandardVersionCreatedDto> CreateVersionAsync(int setId, CreateInspectionStandardVersionRequest request, CancellationToken token);
}

