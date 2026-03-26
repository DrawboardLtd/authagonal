namespace Authagonal.Core.Services;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default);
}
