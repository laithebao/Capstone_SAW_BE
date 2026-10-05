using SAW.Application.Features.Traceability.Dtos;

namespace SAW.Application.Features.Traceability.Interfaces;

public interface ITraceabilityService
{
    Task<PublicTraceabilityResponse> GetAsync(string publicToken, CancellationToken ct);
}
