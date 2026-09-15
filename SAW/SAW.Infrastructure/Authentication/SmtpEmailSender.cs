using System.Net;
using System.Net.Mail;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Configuration;
using SAW.Application.Repositories;

namespace SAW.Infrastructure.Authentication;

public sealed class SmtpEmailSender(IConfiguration configuration) : IEmailSender
{
    public Task SendEmailVerificationAsync(
        string email, string verificationToken, CancellationToken cancellationToken)
    {
        var verificationUrl = BuildUrl(configuration["Smtp:EmailVerificationUrl"], verificationToken);
        return SendAsync(email, "Verify your SAW account",
            $"Use this link to verify your email (valid for 24 hours):\n{verificationUrl}", cancellationToken);
    }

    public Task SendPasswordResetAsync(string email, string resetToken, CancellationToken cancellationToken)
    {
        var resetUrl = BuildUrl(configuration["Smtp:PasswordResetUrl"], resetToken);
        return SendAsync(email, "SAW password reset",
            $"Use this link to reset your password (valid for 24 hours):\n{resetUrl}", cancellationToken);
    }

    private async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        var host = Required("Smtp:Host");
        var fromEmail = Required("Smtp:FromEmail");
        var username = configuration["Smtp:Username"];
        var password = configuration["Smtp:Password"];
        var port = configuration.GetValue("Smtp:Port", 587);

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, configuration["Smtp:FromName"] ?? "Smart Agri-Warehouse"),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(recipient);

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = configuration.GetValue("Smtp:EnableSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(username, password)
        };
        await client.SendMailAsync(message, cancellationToken);
    }

    private string Required(string key)
    {
        var value = configuration[key];
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Configuration '{key}' is required.");
    }

    private static string BuildUrl(string? baseUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("SMTP action URL is not configured.");
        var separator = baseUrl.Contains('?') ? '&' : '?';
        return $"{baseUrl}{separator}token={UrlEncoder.Default.Encode(token)}";
    }
}
