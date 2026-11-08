using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/public/catalog")]
[AllowAnonymous]
public sealed class PublicCatalogController(IDistributorOrderService service) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Get([FromQuery] DistributorQuery query, CancellationToken ct) =>
        Ok(ApiResponse<DistributorPage<PublicCatalogLot>>.Success(await service.PublicCatalogAsync(query, ct)));
}
