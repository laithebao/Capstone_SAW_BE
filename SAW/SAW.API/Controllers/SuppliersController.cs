using Microsoft.AspNetCore.Mvc;

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using static SAW.Application.Features.Suppliers.DTOs.DeclareSupplierProfileRequest;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SUPPLIER")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierCommandService _supplierCommandService;

    public SuppliersController(ISupplierCommandService supplierCommandService)
    {
        _supplierCommandService = supplierCommandService;
    }

    /// <summary>
    /// UC 3.2.41: View Supplier Information Profile
    /// </summary>
    [HttpGet("me/profile")]
    public async Task<ActionResult<SupplierProfileResponse>> GetMyProfile(CancellationToken cancellationToken)
    {
        try
        {
            var accountIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                 ?? User.FindFirst("AccountID")?.Value;

            if (!int.TryParse(accountIdClaim, out int currentAccountId))
            {
                return StatusCode(403, new { message = "You are not allowed to view supplier profile." });
            }

            var response = await _supplierCommandService.GetMyProfileAsync(currentAccountId, cancellationToken);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to load supplier profile." });
        }
    }

    [HttpPost("me/declare")]
    public async Task<ActionResult<SupplierProfileResponse>> DeclareProfile([FromBody] DeclareSupplierProfileRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Please fill in all required fields.", errors = ModelState });
        }

        try
        {
            var accountId = GetCurrentAccountId();
            var result = await _supplierCommandService.DeclareProfileAsync(accountId, request, cancellationToken);

            return CreatedAtAction(nameof(GetMyProfile), new { message = "Supplier information saved successfully.", data = result });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to save supplier information." });
        }
    }

    private int GetCurrentAccountId()
    {
        var accountIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                             ?? User.FindFirst("AccountID")?.Value;

        if (!int.TryParse(accountIdClaim, out int accountId))
        {
            throw new UnauthorizedAccessException("You are not allowed to view/declare supplier profile.");
        }

        return accountId;
    }

    /// <summary>
    /// UC 3.2.43: Edit Supplier Information
    /// </summary>
    [HttpPut("me/profile")]
    public async Task<ActionResult<SupplierProfileResponse>> UpdateMyProfile([FromBody] UpdateSupplierProfileRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Please fill in all required fields.", errors = ModelState });
        }

        try
        {
            var accountId = GetCurrentAccountId();
            var result = await _supplierCommandService.UpdateProfileAsync(accountId, request, cancellationToken);

            return Ok(new { message = "Supplier information updated successfully.", data = result });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message }); // "Supplier profile not found."
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message }); // "This tax code is already registered."
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = "You are not allowed to edit supplier information." });
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Failed to update supplier information." });
        }
    }
}
