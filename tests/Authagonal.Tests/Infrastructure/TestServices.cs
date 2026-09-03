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

/// <summary>
/// Scriptable client-credentials seam. Default: pass-through. Set <see cref="Handler"/> to force claims or
/// reject; every call is recorded so a test can assert what the endpoint forwarded.
/// </summary>
public sealed class TestClientCredentialsClaimsTransformer : Authagonal.Protocol.IClientCredentialsClaimsTransformer
{
    public List<(string ClientId, IReadOnlyList<string> Scopes, IReadOnlyDictionary<string, string> ExtraParameters)> Calls { get; } = [];

    public Func<OAuthClient, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, Authagonal.Protocol.ClientCredentialsClaimsResult>? Handler { get; set; }

    public Task<Authagonal.Protocol.ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default)
    {
        Calls.Add((client.ClientId, grantedScopes, extraParameters));
        return Task.FromResult(Handler?.Invoke(client, grantedScopes, extraParameters)
            ?? Authagonal.Protocol.ClientCredentialsClaimsResult.Allow());
    }
}

public sealed class TestAuthHook : IAuthHook
{
    public List<(string UserId, string Email, string Method)> Authentications { get; } = [];
    public List<(string UserId, string Email, string CreatedVia)> UserCreations { get; } = [];
    public List<(string Email, string Reason)> LoginFailures { get; } = [];
    public List<(string? SubjectId, string ClientId, string GrantType)> TokenIssuances { get; } = [];
    public List<(string UserId, string Email, string MfaMethod)> MfaVerifications { get; } = [];
    public List<(string UserId, string Email, string UpdatedVia)> UserUpdates { get; } = [];
    public List<(string UserId, string Email, string DeletedVia)> UserDeletions { get; } = [];

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

    public Task OnUserUpdatedAsync(string userId, string email, string updatedVia, CancellationToken ct = default)
    {
        UserUpdates.Add((userId, email, updatedVia));
        return Task.CompletedTask;
    }

    public Task OnUserDeletedAsync(string userId, string email, string deletedVia, CancellationToken ct = default)
    {
        UserDeletions.Add((userId, email, deletedVia));
        return Task.CompletedTask;
    }
}

public sealed class TestProvisioningOrchestrator : IProvisioningOrchestrator
{
    /// <summary>Ids of the users provisioning ran for, so a test can prove it did NOT run.</summary>
    public List<string> Provisioned { get; } = [];

    public Task ProvisionAsync(Core.Models.AuthUser user, CancellationToken ct = default)
    {
        Provisioned.Add(user.Id);
        return Task.CompletedTask;
    }

    public Task ProvisionAsync(Core.Models.AuthUser user, IReadOnlyList<string> requiredAppIds, CancellationToken ct = default)
    {
        Provisioned.Add(user.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs inside <see cref="ReprovisionAsync"/>, so a test can act while the round-trip is in flight.
    /// </summary>
    /// <remarks>
    /// The provisioning call is the window that matters on the claim path: it is a network call to a
    /// downstream app and takes seconds, and the handler re-reads the row afterwards precisely because
    /// anything may have happened to it meanwhile. Without a seam here no test can put anything in that
    /// window, which is why the rebase's own failure modes were unreachable from the suite.
    /// </remarks>
    public Func<Core.Models.AuthUser, Task>? DuringReprovision { get; set; }

    public Task ReprovisionAsync(Core.Models.AuthUser user, CancellationToken ct = default)
        => DuringReprovision?.Invoke(user) ?? Task.CompletedTask;

    public Task DeprovisionAllAsync(string userId, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>Mutable token-exchange transformer double: tests set <see cref="OnTransform"/> to
/// inject binding claims / reject / reshape lifetimes; null (default) passes every exchange
/// through unchanged, mirroring <c>NullTokenExchangeSubjectTransformer</c>. Records every call.</summary>
public sealed class TestTokenExchangeSubjectTransformer : Authagonal.Protocol.ITokenExchangeSubjectTransformer
{
    /// <summary>Every call. <c>SubjectClientId</c> is the client that obtained the subject token,
    /// which is not the same as <c>ClientId</c> (the client performing the exchange).</summary>
    public List<(string SubjectId, string ClientId, IReadOnlyList<string> Scopes, IReadOnlyDictionary<string, string> ExtraParameters, string? SubjectClientId)> Calls { get; } = [];

    public Func<Authagonal.Protocol.OidcSubject, OAuthClient, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, Authagonal.Protocol.OidcSubjectResult>? OnTransform { get; set; }

    public Task<Authagonal.Protocol.OidcSubjectResult> TransformAsync(
        Authagonal.Protocol.OidcSubject subject,
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        Authagonal.Protocol.TokenExchangeContext context,
        CancellationToken ct = default)
    {
        Calls.Add((subject.SubjectId, client.ClientId, grantedScopes, extraParameters, context.SubjectClientId));
        var result = OnTransform?.Invoke(subject, client, grantedScopes, extraParameters)
            ?? Authagonal.Protocol.OidcSubjectResult.Allow(subject);
        return Task.FromResult(result);
    }
}

/// <summary>
/// Records every audit row an admin write produces, so a test can assert the write is attributable.
/// </summary>
/// <remarks>
/// The factory registers this in place of <c>NullAuditLogger</c> — which is what AddAuthagonal
/// TryAddSingletons — because the interesting property of an admin mutation is not only that it happened
/// but that the trail names who did it. Several of the highest-impact writes (SCIM token mint, SSO
/// connection create, role and scope edits) produced no row at all.
/// </remarks>
public sealed class RecordingAuditLogger : IAuditLogger
{
    public List<(string Actor, string Action, string EntityType, string? EntityId, string? Detail)> Entries { get; } = [];

    public Task LogAsync(string actor, string action, string entityType, string? entityId = null, string? detail = null, CancellationToken ct = default)
    {
        Entries.Add((actor, action, entityType, entityId, detail));
        return Task.CompletedTask;
    }

    public bool Has(string action) => Entries.Any(e => e.Action == action);
}

/// <summary>
/// Captures every log record the host writes, so a test can assert what is NOT in the log.
/// </summary>
/// <remarks>
/// "This value must never reach the log" is a guard like any other, and the only way to hold it is to
/// read the log back. The SCIM bearer handler wrote the presented token's length and a hash prefix on
/// every request, successes included — a per-token fingerprint for anyone with log read access.
/// </remarks>
public sealed class RecordingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Sink(_messages);

    public void Dispose() { }

    private sealed class Sink(System.Collections.Concurrent.ConcurrentQueue<string> messages)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }
}
