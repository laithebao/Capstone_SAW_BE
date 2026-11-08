using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.QrCodes.Dtos;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/operation/product-batches/{id:long}/qr-code")]
[Authorize(Roles = "OPERATION_STAFF,QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
public sealed class ProductBatchQrCodesController(IProductBatchQrCodeService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProductBatchQrCodeResponse>>> Get(long id, CancellationToken ct) =>
        Ok(ApiResponse<ProductBatchQrCodeResponse>.Success(await service.GetAsync(id, ct)));
}
