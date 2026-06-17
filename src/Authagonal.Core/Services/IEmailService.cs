namespace Authagonal.Core.Services;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default);

    /// <summary>
    /// Sent when someone attempts to register an email that already has an account. Lets the real
    /// owner sign in / reset their password, while the registration endpoint returns the same neutral
    /// response whether or not the email existed (no account enumeration). Default no-op so existing
    /// implementors keep compiling.
    /// </summary>
    Task SendAccountExistsEmailAsync(string email, string signInUrl, CancellationToken ct = default)
        => Task.CompletedTask;
}
