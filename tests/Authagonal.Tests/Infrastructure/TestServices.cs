using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.Tests.Infrastructure;

public sealed class TestTenantContext(string issuer) : ITenantContext
{
    public string TenantId => "test";
    public string Issuer => issuer;
}


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
    public List<(string UserId, string Email, string MfaMethod)> MfaVerifications { get; } = [];

    /// <summary>Set to override MFA policy resolution. Null = return clientPolicy unchanged.</summary>
    public Func<string, string, MfaPolicy, string, MfaPolicy>? MfaPolicyOverride { get; set; }

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

    public Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email, MfaPolicy clientPolicy, string clientId, CancellationToken ct = default)
    {
        var result = MfaPolicyOverride?.Invoke(userId, email, clientPolicy, clientId) ?? clientPolicy;
        return Task.FromResult(result);
    }

    public Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod, CancellationToken ct = default)
    {
        MfaVerifications.Add((userId, email, mfaMethod));
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
