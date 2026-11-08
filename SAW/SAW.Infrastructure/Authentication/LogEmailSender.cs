using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAW.Application.Repositories;

namespace SAW.Infrastructure.Authentication;

/// <summary>
/// Development-only email delivery. Links are written to the local API log so
/// registration and password reset can be tested without real SMTP credentials.
/// </summary>
public sealed class LogEmailSender(
    IConfiguration configuration,
    ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendEmailVerificationAsync(
        string email,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(configuration["Smtp:EmailVerificationUrl"], verificationToken);
        logger.LogInformation(
            "Development email verification for {Email}: {VerificationUrl}", email, url);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(
        string email,
        string resetToken,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(configuration["Smtp:PasswordResetUrl"], resetToken);
        logger.LogInformation(
            "Development password reset for {Email}: {ResetUrl}", email, url);
        return Task.CompletedTask;
    }

    private static string BuildUrl(string? baseUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Email action URL is not configured.");

        var separator = baseUrl.Contains('?') ? '&' : '?';
        return $"{baseUrl}{separator}token={Uri.EscapeDataString(token)}";
    }
}
