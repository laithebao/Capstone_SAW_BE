using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using System.Security.Claims;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SUPPLIER")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierCommandService _supplierCommandService;
    private readonly ILogger<SuppliersController> _logger;

    public SuppliersController(
        ISupplierCommandService supplierCommandService,
        ILogger<SuppliersController> logger)
    {
        _supplierCommandService = supplierCommandService;
        _logger = logger;
    }

    /// <summary>
    /// UC 3.2.41: View Supplier Information Profile
    /// </summary>
    [HttpGet("me/profile")]
    public async Task<ActionResult<SupplierProfileResponse>> GetMyProfile(CancellationToken cancellationToken)
    {
        try
        {
            var currentAccountId = GetCurrentAccountId();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi lấy thông tin Supplier Profile");
            return StatusCode(500, new { message = "Failed to load supplier profile.", detail = ex.Message });
        }
    }

    /// <summary>
    /// UC 3.2.42: Declare Supplier Profile
    /// </summary>
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

            return CreatedAtAction(nameof(GetMyProfile), null, result);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi khai báo thông tin Supplier");
            return StatusCode(500, new { message = "Failed to save supplier information.", detail = ex.Message });
        }
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

            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi 500 khi cập nhật Supplier Profile");
            return StatusCode(500, new { message = "Failed to update supplier information.", detail = ex.Message });
        }
    }

    private int GetCurrentAccountId()
    {
        var accountIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                             ?? User.FindFirst("AccountID")?.Value;

        if (!int.TryParse(accountIdClaim, out int accountId))
        {
            throw new UnauthorizedAccessException("You are not allowed to perform this action.");
        }

        return accountId;
    }
}