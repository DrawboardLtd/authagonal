using Authagonal.Core.Services;

namespace Authagonal.Tests.Infrastructure;

public sealed class TestEmailService : IEmailService
{
    public List<(string Email, string CallbackUrl, string Type)> SentEmails { get; } = [];

    public Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        SentEmails.Add((email, callbackUrl, "verification"));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        SentEmails.Add((email, callbackUrl, "password_reset"));
        return Task.CompletedTask;
    }
}

public sealed class TestAuthHook : IAuthHook
{
    public List<(string UserId, string Email, string Method)> Authentications { get; } = [];
    public List<(string UserId, string Email, string CreatedVia)> UserCreations { get; } = [];
    public List<(string Email, string Reason)> LoginFailures { get; } = [];
    public List<(string? SubjectId, string ClientId, string GrantType)> TokenIssuances { get; } = [];

    public Task OnUserAuthenticatedAsync(string userId, string email, string method, string? clientId = null, CancellationToken ct = default)
    {
        Authentications.Add((userId, email, method));
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email, string createdVia, CancellationToken ct = default)
    {
        UserCreations.Add((userId, email, createdVia));
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct = default)
    {
        LoginFailures.Add((email, reason));
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType, CancellationToken ct = default)
    {
        TokenIssuances.Add((subjectId, clientId, grantType));
        return Task.CompletedTask;
    }
}

public sealed class TestProvisioningOrchestrator : IProvisioningOrchestrator
{
    public Task ProvisionAsync(Core.Models.AuthUser user, IReadOnlyList<string> requiredAppIds, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeprovisionAllAsync(string userId, CancellationToken ct = default)
        => Task.CompletedTask;
}
