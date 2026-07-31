using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

/// <summary>
/// Hook into the authentication lifecycle. Implementations are called at key points
/// during authentication and can influence outcomes by throwing exceptions to abort operations.
/// Multiple implementations can be registered — all will run in registration order.
/// </summary>
public interface IAuthHook
{
    /// <summary>Called after a user successfully authenticates (password, SAML, or OIDC).
    /// Throw to reject the authentication.</summary>
    /// <param name="method">One of "password", "saml", or "oidc".</param>
    Task OnUserAuthenticatedAsync(string userId, string email, string method, string? clientId = null, CancellationToken ct = default);

    /// <summary>Called after a new user record is created.
    /// Throw to abort (the user will still exist — handle rollback if needed).</summary>
    /// <param name="createdVia">One of "admin", "saml", or "oidc".</param>
    Task OnUserCreatedAsync(string userId, string email, string createdVia, CancellationToken ct = default);

    /// <summary>Called when a login attempt fails (bad password, locked out, etc.).</summary>
    Task OnLoginFailedAsync(string email, string reason, CancellationToken ct = default);

    /// <summary>Called when tokens are issued via the token endpoint.
    /// Throw to reject the token issuance.</summary>
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType, CancellationToken ct = default);

    /// <summary>
    /// Called after password verification to resolve the effective MFA policy for the user.
    /// Override to enforce MFA per-user/org regardless of client setting, or to exempt service accounts.
    /// Default: returns clientPolicy unchanged.
    /// </summary>
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email, MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);

    /// <summary>Called after a user successfully completes MFA verification.</summary>
    /// <param name="mfaMethod">One of "totp", "webauthn", or "recovery".</param>
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod, CancellationToken ct = default);

    /// <summary>An MFA verification attempt failed (wrong TOTP code, unmatched recovery code, failed
    /// WebAuthn assertion). Distinct from <see cref="OnLoginFailedAsync"/>, which is the password
    /// stage — this fires only after valid first-factor credentials, so bursts are a strong
    /// MFA-bypass-attempt signal.</summary>
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called after a user record is updated (profile fields, organization, active status, etc.).
    /// Notification only — the update has already happened.</summary>
    /// <param name="updatedVia">Origin of the update, e.g. "portal", "scim", "self-service".</param>
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia, CancellationToken ct = default);

    /// <summary>Called after a user record is deleted. Notification only — implementations
    /// must not assume the record is still readable.</summary>
    /// <param name="deletedVia">Origin of the deletion, e.g. "portal", "scim".</param>
    Task OnUserDeletedAsync(string userId, string email, string deletedVia, CancellationToken ct = default);

    /// <summary>Called after a user confirms their email via the verification link.
    /// Notification only — the confirmation has already been persisted. Default: no-op,
    /// so existing hooks need no change.</summary>
    Task OnEmailConfirmedAsync(string userId, string email, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called after a user adds a new MFA factor (enrols a TOTP authenticator or a passkey).
    /// Notification only — the credential is already active. Default: no-op.</summary>
    /// <param name="mfaMethod">One of "totp" or "webauthn".</param>
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called after a user removes one of their MFA credentials. Notification only.
    /// Default: no-op.</summary>
    /// <param name="mfaMethod">The removed credential's type ("totp", "webauthn", or "recoverycode").</param>
    /// <param name="mfaDisabled">True when removing this credential left no primary factor, so MFA
    /// was switched off for the user entirely.</param>
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod, bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called after a user regenerates their MFA recovery codes (the previous set is
    /// invalidated). Notification only. Default: no-op.</summary>
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called after a user's password is changed. Notification only — the change is already
    /// persisted and existing sessions invalidated. Default: no-op.</summary>
    /// <param name="changedVia">Origin of the change, e.g. "reset".</param>
    Task OnPasswordChangedAsync(string userId, string email, string changedVia, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Richer pre-mint gate than <see cref="OnTokenIssuedAsync"/>: fires with the resolved
    /// subject, scope set, and requested authority once they are known (delegated exchanges
    /// and agent client_credentials mints). Throw to reject the issuance. Default: no-op —
    /// the three-argument <see cref="OnTokenIssuedAsync"/> keeps firing unchanged.
    /// </summary>
    Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called after a delegated (composite-identity) token is minted via token
    /// exchange. Notification only — the token is already issued. Default: no-op.</summary>
    Task OnDelegationMintedAsync(DelegationAudit audit, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called when a delegated exchange parks on an ask-policy action and a pending
    /// approval is created. The host's notification channel (email/push/chat) hangs off this
    /// event. Default: no-op.</summary>
    Task OnApprovalRequestedAsync(ApprovalAudit audit, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called when a pending approval is resolved (approved or denied) by the user.
    /// Default: no-op.</summary>
    Task OnApprovalResolvedAsync(ApprovalAudit audit, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called when a user grants or revokes standing consent for an agent.
    /// Default: no-op.</summary>
    /// <param name="change">"granted" or "revoked".</param>
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called when a user revokes an authorized app from the Authorized Apps screen.
    /// Notification only — the consent record and the client's session-bound grants are already
    /// gone. Distinct from <see cref="OnAgentConsentChangedAsync"/>, which covers the agentic
    /// (RFC 9396) standing consents. Default: no-op.</summary>
    /// <param name="grantsRemoved">How many session-bound grants (refresh tokens, codes, device
    /// codes, PAR requests) were removed along with the consent — 0 means the app held none.</param>
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called when a capability ticket is redeemed for its bound token.
    /// Default: no-op.</summary>
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Pre-mint context for <see cref="IAuthHook.OnTokenIssuingAsync"/>.</summary>
/// <param name="RequestedAuthorityJson">The requested authorization_details (RFC 9396 JSON
/// array), when present.</param>
public sealed record TokenIssuanceContext(
    string ClientId,
    string? SubjectId,
    string GrantType,
    IReadOnlyList<string> Scopes,
    string? RequestedAuthorityJson);

/// <summary>Audit payload for a minted delegation. <paramref name="ActorChain"/> is outermost
/// (current actor) first; <paramref name="EffectiveAuthorityJson"/> is the RFC 9396 array the
/// token carries.</summary>
public sealed record DelegationAudit(
    string SubjectId,
    IReadOnlyList<string> ActorChain,
    string EffectiveAuthorityJson,
    DateTimeOffset ExpiresAt,
    string? ApprovalId);

/// <summary>Audit payload for an approval lifecycle event.</summary>
public sealed record ApprovalAudit(
    string ApprovalId,
    string ClientId,
    string SubjectId,
    IReadOnlyList<string> PendingActions,
    string Status);

/// <summary>
/// Extension methods for running all hooks in an <see cref="IEnumerable{IAuthHook}"/> pipeline.
/// </summary>
public static class AuthHookExtensions
{
    public static async Task RunOnUserAuthenticatedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string method, string? clientId = null, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnUserAuthenticatedAsync(userId, email, method, clientId, ct);
    }

    public static async Task RunOnUserCreatedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string createdVia, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnUserCreatedAsync(userId, email, createdVia, ct);
    }

    public static async Task RunOnLoginFailedAsync(this IEnumerable<IAuthHook> hooks, string email, string reason, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnLoginFailedAsync(email, reason, ct);
    }

    public static async Task RunOnTokenIssuedAsync(this IEnumerable<IAuthHook> hooks, string? subjectId, string clientId, string grantType, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnTokenIssuedAsync(subjectId, clientId, grantType, ct);
    }

    public static async Task<MfaPolicy> RunResolveMfaPolicyAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, MfaPolicy clientPolicy, string clientId, CancellationToken ct = default)
    {
        var policy = clientPolicy;
        foreach (var hook in hooks)
            policy = await hook.ResolveMfaPolicyAsync(userId, email, policy, clientId, ct);
        return policy;
    }

    public static async Task RunOnMfaVerifiedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string mfaMethod, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnMfaVerifiedAsync(userId, email, mfaMethod, ct);
    }

    public static async Task RunOnMfaVerifyFailedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string mfaMethod, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnMfaVerifyFailedAsync(userId, email, mfaMethod, ct);
    }

    public static async Task RunOnUserUpdatedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string updatedVia, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnUserUpdatedAsync(userId, email, updatedVia, ct);
    }

    public static async Task RunOnUserDeletedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string deletedVia, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnUserDeletedAsync(userId, email, deletedVia, ct);
    }

    public static async Task RunOnEmailConfirmedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnEmailConfirmedAsync(userId, email, ct);
    }

    public static async Task RunOnMfaEnrolledAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string mfaMethod, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnMfaEnrolledAsync(userId, email, mfaMethod, ct);
    }

    public static async Task RunOnMfaCredentialRemovedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string mfaMethod, bool mfaDisabled, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnMfaCredentialRemovedAsync(userId, email, mfaMethod, mfaDisabled, ct);
    }

    public static async Task RunOnRecoveryCodesRegeneratedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnRecoveryCodesRegeneratedAsync(userId, email, ct);
    }

    public static async Task RunOnPasswordChangedAsync(this IEnumerable<IAuthHook> hooks, string userId, string email, string changedVia, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnPasswordChangedAsync(userId, email, changedVia, ct);
    }

    public static async Task RunOnTokenIssuingAsync(this IEnumerable<IAuthHook> hooks, TokenIssuanceContext context, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnTokenIssuingAsync(context, ct);
    }

    public static async Task RunOnDelegationMintedAsync(this IEnumerable<IAuthHook> hooks, DelegationAudit audit, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnDelegationMintedAsync(audit, ct);
    }

    public static async Task RunOnApprovalRequestedAsync(this IEnumerable<IAuthHook> hooks, ApprovalAudit audit, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnApprovalRequestedAsync(audit, ct);
    }

    public static async Task RunOnApprovalResolvedAsync(this IEnumerable<IAuthHook> hooks, ApprovalAudit audit, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnApprovalResolvedAsync(audit, ct);
    }

    public static async Task RunOnAgentConsentChangedAsync(this IEnumerable<IAuthHook> hooks, string subjectId, string clientId, string change, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnAgentConsentChangedAsync(subjectId, clientId, change, ct);
    }

    public static async Task RunOnConsentRevokedAsync(this IEnumerable<IAuthHook> hooks, string subjectId, string clientId, int grantsRemoved, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnConsentRevokedAsync(subjectId, clientId, grantsRemoved, ct);
    }

    public static async Task RunOnCapabilityTicketRedeemedAsync(this IEnumerable<IAuthHook> hooks, string ticketId, string? subjectId, string clientId, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
            await hook.OnCapabilityTicketRedeemedAsync(ticketId, subjectId, clientId, ct);
    }
}
