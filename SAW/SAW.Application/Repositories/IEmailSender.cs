namespace SAW.Application.Repositories;

public interface IEmailSender
{
    Task SendEmailVerificationAsync(string email, string verificationToken, CancellationToken cancellationToken);
    Task SendPasswordResetAsync(string email, string resetToken, CancellationToken cancellationToken);
}
