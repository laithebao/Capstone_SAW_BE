using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SUPPLIER")]
public class SupplierBatchesController : ControllerBase
{
    private readonly ISupplierBatchCommandService _supplierBatchCommandService;

    public SupplierBatchesController(ISupplierBatchCommandService supplierBatchCommandService)
    {
        _supplierBatchCommandService = supplierBatchCommandService;
    }

    /// <summary>
    /// UC 3.2.44: View Declared Product Batch List
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<SupplierBatchListResponse>> GetDeclaredBatches([FromQuery] GetSupplierBatchesQueryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var accountId = GetCurrentAccountId();
            var response = await _supplierBatchCommandService.GetDeclaredBatchesAsync(accountId, request, cancellationToken);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message }); // "Supplier profile not found."
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { message = "You are not allowed to view declared product batches." });
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to load declared product batch list." });
        }
    }

    private int GetCurrentAccountId()
    {
        var accountIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                             ?? User.FindFirst("AccountID")?.Value;

        if (!int.TryParse(accountIdClaim, out int accountId))
        {
            throw new UnauthorizedAccessException();
        }

        return accountId;
    }
}