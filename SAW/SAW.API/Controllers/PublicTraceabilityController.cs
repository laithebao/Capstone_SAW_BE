using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.Traceability.Dtos;
using SAW.Application.Features.Traceability.Interfaces;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/public/traceability")]
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PublicTraceabilityController(ITraceabilityService service) : ControllerBase
{
    [HttpGet("{publicToken}")]
    public async Task<ActionResult<ApiResponse<PublicTraceabilityResponse>>> Get(string publicToken, CancellationToken ct) =>
        Ok(ApiResponse<PublicTraceabilityResponse>.Success(await service.GetAsync(publicToken, ct)));
}
