using SAW.Application.Features.Traceability.Dtos;

namespace SAW.Application.Repositories;

public interface ITraceabilityRepository
{
    Task<TraceabilityData?> GetByPublicTokenAsync(string publicToken, CancellationToken ct);
}
