using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Features.Authentication.DTOs;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthCommandService authService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, GetIpAddress(), cancellationToken);
        return Ok(ApiResponse<AuthResponse>.Success(result, "Login successfully."));
    }

    [HttpPost("google")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<GoogleLoginResponse>>> GoogleLogin(
        GoogleLoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.GoogleLoginAsync(request, GetIpAddress(), cancellationToken);
        return Ok(ApiResponse<GoogleLoginResponse>.Success(result));
    }

    [HttpPost("refresh-token")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> RefreshToken(
        RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.RefreshTokenAsync(request, GetIpAddress(), cancellationToken);
        return Ok(ApiResponse<AuthResponse>.Success(result, "Token refreshed successfully."));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(request, GetIpAddress(), cancellationToken);
        return Ok(ApiResponse.Success("Logged out successfully."));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<RegistrationResponse>>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.RegisterAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<RegistrationResponse>.Created(result, "Account registered successfully."));
    }

    [HttpPost("email/verify")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> VerifyEmail(
        VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        await authService.VerifyEmailAsync(request, cancellationToken);
        return Ok(ApiResponse.Success("Email verified successfully."));
    }

    [HttpPost("password/forgot")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.RequestPasswordResetAsync(request, cancellationToken);
        return Ok(ApiResponse.Success("If the email exists, a password reset link has been sent."));
    }

    [HttpPost("password/reset")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(request, cancellationToken);
        return Ok(ApiResponse.Success("Password reset successfully."));
    }

    [HttpPost("password/change")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var accountIdValue = User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(accountIdValue, out var accountId)) throw new UnauthorizedAccessException();
        await authService.ChangePasswordAsync(accountId, request, cancellationToken);
        return Ok(ApiResponse.Success("Password changed successfully. Please log in again."));
    }

    private string? GetIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
