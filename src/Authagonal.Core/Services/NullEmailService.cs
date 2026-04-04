namespace Authagonal.Core.Services;

/// <summary>
/// No-op email service used when no email provider is configured.
/// Silently discards all emails. Register a real <see cref="IEmailService"/>
/// before calling <c>AddAuthagonal</c> to enable email delivery.
/// </summary>
public sealed class NullEmailService : IEmailService
{
    public Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
        => Task.CompletedTask;
}
