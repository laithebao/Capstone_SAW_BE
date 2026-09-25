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

    /// <summary>
    /// UC 3.2.45: Declare Product Batch Information
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SupplierBatchItemResponse>> DeclareBatch([FromBody] DeclareProductBatchRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Please fill in all required fields.", errors = ModelState });
        }

        try
        {
            var accountId = GetCurrentAccountId();
            var result = await _supplierBatchCommandService.DeclareBatchAsync(accountId, request, cancellationToken);

            return CreatedAtAction(nameof(GetDeclaredBatches), new { id = result.BatchId }, new { message = "Product batch information declared successfully.", data = result });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message }); // "Please declare supplier information..."
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message }); // "The selected crop type is not registered..."
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { message = "You are not allowed to declare product batch information." });
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to declare product batch information." });
        }
    }

    /// <summary>
    /// UC 3.2.46: Edit Declared Product Batch Information
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<ActionResult<SupplierBatchItemResponse>> UpdateDeclaredBatch([FromRoute] long id, [FromBody] UpdateProductBatchRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Please fill in all required fields.", errors = ModelState });
        }

        try
        {
            var accountId = GetCurrentAccountId();
            var result = await _supplierBatchCommandService.UpdateDeclaredBatchAsync(id, accountId, request, cancellationToken);

            return Ok(new { message = "Declared product batch updated successfully.", data = result });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message }); // "Product batch not found."
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message }); // "You are not allowed to edit this batch."
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message }); // "This batch declaration can no longer be modified."
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to update declared product batch information." });
        }
    }

    /// <summary>
    /// UC 3.2.47: View Current Batch Status
    /// </summary>
    [HttpGet("{id:long}/status")]
    public async Task<ActionResult<SupplierBatchStatusResponse>> GetBatchStatus([FromRoute] long id, CancellationToken cancellationToken)
    {
        try
        {
            var accountId = GetCurrentAccountId();
            var result = await _supplierBatchCommandService.GetBatchStatusDetailAsync(id, accountId, cancellationToken);

            return Ok(new { message = "Batch status retrieved successfully.", data = result });
        }
        catch (UnauthorizedAccessException ex) when (ex.Message.Contains("Supplier profile not found"))
        {
            return StatusCode(403, new { message = "Supplier profile not found." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = "You are not allowed to view this batch." });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message }); // "Product batch not found."
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to retrieve batch information." });
        }
    }

    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> CancelBatch([FromRoute] long id, CancellationToken cancellationToken)
    {
        try
        {
            var accountId = GetCurrentAccountId();
            await _supplierBatchCommandService.CancelBatchAsync(id, accountId, cancellationToken);

            return Ok(new { message = "Product batch cancelled successfully." });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message }); // "Product batch not found."
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message }); // "You are not allowed to cancel this batch."
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message }); // "This batch cannot be cancelled at its current status."
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to cancel product batch." });
        }
    }
}