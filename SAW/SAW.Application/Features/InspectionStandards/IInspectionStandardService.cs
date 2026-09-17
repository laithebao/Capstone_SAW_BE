namespace SAW.Application.Features.InspectionStandards;
public interface IInspectionStandardService { Task<IReadOnlyList<InspectionStandardListItem>> ListAsync(string? search, CancellationToken token); Task<InspectionStandardDto> CreateAsync(CreateInspectionStandardRequest request, CancellationToken token); }
