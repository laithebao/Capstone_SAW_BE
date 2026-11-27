namespace SAW.Application.Repositories;

public interface IEmailSender
{
    Task SendBusinessAsync(string email, string subject, string body, string messageId, CancellationToken cancellationToken);
    Task SendEmailVerificationAsync(string email, string verificationToken, CancellationToken cancellationToken);
    Task SendPasswordResetAsync(string email, string resetToken, CancellationToken cancellationToken);
}
