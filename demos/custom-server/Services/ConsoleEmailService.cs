using Authagonal.Core.Services;

namespace CustomAuthServer.Services;

/// <summary>
/// Example IEmailService that writes emails to the console instead of sending them.
/// Perfect for local development and demos. Replace with SMTP, SendGrid, SES, etc.
/// </summary>
public sealed class ConsoleEmailService(ILogger<ConsoleEmailService> logger) : IEmailService
{
    public Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        logger.LogInformation(
            """
            ╔══════════════════════════════════════════════════╗
            ║  EMAIL: Verify your email address                ║
            ╠══════════════════════════════════════════════════╣
            ║  To:   {Email}
            ║  Link: {CallbackUrl}
            ╚══════════════════════════════════════════════════╝
            """,
            email, callbackUrl);

        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        logger.LogInformation(
            """
            ╔══════════════════════════════════════════════════╗
            ║  EMAIL: Reset your password                      ║
            ╠══════════════════════════════════════════════════╣
            ║  To:   {Email}
            ║  Link: {CallbackUrl}
            ╚══════════════════════════════════════════════════╝
            """,
            email, callbackUrl);

        return Task.CompletedTask;
    }
}
