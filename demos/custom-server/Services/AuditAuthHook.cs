using Authagonal.Core.Services;

namespace CustomAuthServer.Services;

/// <summary>
/// Example IAuthHook that logs every authentication event.
/// In a real deployment you might write to a database, emit metrics,
/// send Slack/Teams notifications, or call a webhook.
///
/// Throwing from any method aborts the operation — for example, throwing
/// from OnUserAuthenticatedAsync would reject the login even though the
/// credentials were valid.
/// </summary>
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email, string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation(
            "[AUDIT] User authenticated: userId={UserId}, email={Email}, method={Method}, clientId={ClientId}",
            userId, email, method, clientId ?? "(none)");

        // Example: reject authentication for a specific domain
        // if (email.EndsWith("@blocked.example.com"))
        //     throw new InvalidOperationException("Domain is not allowed");

        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email, string createdVia, CancellationToken ct)
    {
        logger.LogInformation(
            "[AUDIT] User created: userId={UserId}, email={Email}, via={CreatedVia}",
            userId, email, createdVia);

        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning(
            "[AUDIT] Login failed: email={Email}, reason={Reason}",
            email, reason);

        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType, CancellationToken ct)
    {
        logger.LogInformation(
            "[AUDIT] Token issued: subjectId={SubjectId}, clientId={ClientId}, grantType={GrantType}",
            subjectId ?? "(client-credentials)", clientId, grantType);

        return Task.CompletedTask;
    }
}
