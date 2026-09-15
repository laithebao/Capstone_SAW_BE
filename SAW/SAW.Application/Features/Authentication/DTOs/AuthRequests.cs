using System.ComponentModel.DataAnnotations;

namespace SAW.Application.Features.Authentication.DTOs;

public sealed record LoginRequest(
    [Required, MaxLength(255)] string UsernameOrEmail,
    [Required] string Password);

public sealed record RegisterRequest(
    [Required, MaxLength(100)] string Username,
    [Required, EmailAddress, MaxLength(255)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password,
    [Required, MaxLength(150)] string FullName,
    [Phone, MaxLength(30)] string? PhoneNumber,
    [Range(1, int.MaxValue)] int RoleId,
    [Required, MaxLength(200)] string OrganizationName,
    [Required, MaxLength(50)] string TaxCode,
    [MaxLength(500)] string? Address);

public sealed record ForgotPasswordRequest(
    [Required, EmailAddress, MaxLength(255)] string Email);

public sealed record ResetPasswordRequest(
    [Required] string Token,
    [Required, MinLength(8), MaxLength(128)] string NewPassword);

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(8), MaxLength(128)] string NewPassword);

public sealed record LogoutRequest([Required] string RefreshToken);

public sealed record RefreshTokenRequest([Required] string RefreshToken);

public sealed record VerifyEmailRequest([Required] string Token);

public sealed record GoogleLoginRequest(
    [Required] string IdToken,
    [Range(1, int.MaxValue)] int? RoleId);
