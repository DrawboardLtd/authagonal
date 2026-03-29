using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

/// <summary>
/// Hook into the authentication lifecycle. Implementations are called at key points
/// during authentication and can influence outcomes by throwing exceptions to abort operations.
/// The default implementation is a no-op.
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
}
