namespace Authagonal.Server.Services;

/// <summary>
/// Configuration for authentication, rate limiting, and token expiry settings.
/// Bound from the "Auth" configuration section.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// Allows the OAuth endpoints (<c>/connect/*</c>) to answer plaintext http requests. Default
    /// false: a non-https request to those endpoints is refused with <c>invalid_request</c>.
    /// </summary>
    /// <remarks>
    /// RFC 6749 §3.1 and §3.2 require TLS at the authorization and token endpoints, and the reason is
    /// not ceremonial — a plaintext exchange hands anyone on the path the authorization code, the
    /// client secret in the Basic header, and the access and refresh tokens that come back. The scheme
    /// is read after forwarded-header processing, so terminating TLS at a proxy that sends
    /// <c>X-Forwarded-Proto: https</c> satisfies the gate; only a genuinely plaintext deployment needs
    /// this switch. It is deliberately explicit rather than inferred from the environment name: the
    /// shipped docker-compose and the test harness both speak http and both set it, and an operator
    /// who runs plaintext anywhere else has to say so.
    /// </remarks>
    public bool AllowInsecureHttp { get; set; }

    // --- Account lockout ---
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 10;

    // --- Registration rate limiting ---
    /// <summary>
    /// Password attempts allowed per source address per <see cref="LoginWindowMinutes"/>.
    /// </summary>
    /// <remarks>
    /// Per-account lockout does not bound SPRAYING: one attempt each against ten thousand accounts never
    /// trips any account's counter. And because an unknown email is verified against a dummy hash to keep
    /// the response timing uniform (which is the right call — it prevents enumeration), every unauthenticated
    /// request pays a full PBKDF2, so the endpoint is a CPU amplifier as well. This is the bound on both.
    /// Generous enough for a household behind one NAT address.
    /// </remarks>
    public int MaxLoginAttemptsPerIp { get; set; } = 30;

    /// <summary>Window for <see cref="MaxLoginAttemptsPerIp"/>.</summary>
    public int LoginWindowMinutes { get; set; } = 5;

    public int MaxRegistrationsPerIp { get; set; } = 5;
    public int RegistrationWindowMinutes { get; set; } = 60;

    /// <summary>
    /// When true, registering an email that belongs to an existing account WITH NO LOCAL CREDENTIAL
    /// (a federated / JIT-provisioned account, PasswordHash null) STAGES a password on that account
    /// instead of returning the enumeration-neutral duplicate response. The staged credential and the
    /// claim's profile/attributes stay inert until the claimant clicks a fresh verification email — only
    /// then are they promoted and downstream provisioning re-runs — so merely KNOWING a federated
    /// account's email can't take it over. An account that ALREADY has a password is never affected —
    /// a re-register can never overwrite a real credential. Default OFF: a generic deployment treats
    /// every existing email as a duplicate.
    /// </summary>
    public bool AllowPasswordlessAccountClaim { get; set; }

    /// <summary>
    /// Custom-attribute keys a passwordless claim (<see cref="AllowPasswordlessAccountClaim"/>) may carry
    /// from the register request onto the claimed account. These reach downstream provisioning and can
    /// ride the real owner's tokens, so restricting them limits what a claim can inject. Empty (default)
    /// allows all keys — back-compat; list specific keys to restrict a claim to an expected set. Only
    /// consulted when a claim actually stages attributes.
    /// </summary>
    public List<string> ClaimAllowedAttributeKeys { get; set; } = [];

    // --- Password-reset rate limiting (per target email, so one address can't be email-bombed
    //     regardless of source IP) ---
    public int MaxPasswordResetsPerEmail { get; set; } = 3;
    public int PasswordResetWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Email domains whose self-service registrations are auto-confirmed (skip the verification
    /// email). Empty by default — every registration must verify its email. Intended only for
    /// dev/test (e.g. <c>["example.com"]</c>); never list a domain that can receive real mail.
    /// </summary>
    public List<string> AutoConfirmEmailDomains { get; set; } = [];

    // --- Token / link expiry ---
    public int EmailVerificationExpiryHours { get; set; } = 24;
    public int PasswordResetExpiryMinutes { get; set; } = 60;
    public int MfaChallengeExpiryMinutes { get; set; } = 5;
    public int MfaSetupTokenExpiryMinutes { get; set; } = 15;

    /// <summary>
    /// Hosts permitted to act as a WebAuthn relying party. Empty (the default) accepts any host, which
    /// preserves existing deployments but leaves the origin and rpIdHash checks self-referential.
    /// </summary>
    /// <remarks>
    /// The RP ID and expected origin are derived from the request's own Host header, so without an
    /// allowlist the values a ceremony is validated AGAINST come from the request being validated.
    /// Set this to the real host list; on a multi-tenant deployment, to every tenant host.
    /// </remarks>
    public List<string> WebAuthnAllowedHosts { get; set; } = [];

    // --- Password hashing ---

    /// <summary>
    /// PBKDF2-HMAC-SHA256 work factor for newly written hashes. OWASP's 2026 Password Storage
    /// guidance is 600,000.
    /// </summary>
    /// <remarks>
    /// Raising this is now safe and is the intended way to keep pace: the cost is recorded in each
    /// hash (<c>PBKDF2v2$</c>), verification derives at the cost the hash carries, and a hash below
    /// this target is transparently re-written on the owner's next successful login. Previously the
    /// count was not stored, so verification re-derived at whatever this said and changing it
    /// invalidated every hash in the deployment at once — which is why the default sat at 100,000.
    /// </remarks>
    public int Pbkdf2Iterations { get; set; } = 600_000;

    /// <summary>
    /// Lower bound enforced at startup, so a typo or a copied dev config cannot silently weaken
    /// every password and client secret the deployment writes from then on.
    /// </summary>
    public const int MinimumPbkdf2Iterations = 100_000;

    /// <summary>
    /// Wall-clock floor, in milliseconds, that a failed login is held to before the uniform
    /// <c>invalid_credentials</c> response is written, measured from the start of the request.
    /// Closes the user-enumeration timing oracle: the no-such-user path verifies against a dummy
    /// hash in the native PBKDF2 format, but a real account may hold a bcrypt or ASP.NET Identity V3
    /// hash at a completely different cost, so equal work is not achievable and equal elapsed time
    /// is. Must sit above the slowest password hash the deployment holds or the pad is a no-op —
    /// raise it if you imported bcrypt hashes above cost 11 or raised <see cref="Pbkdf2Iterations"/>
    /// well past the default. A single warning is logged the first time a failed login overruns it.
    /// Set to 0 to disable the pad entirely (re-opens the oracle; intended only for load testing).
    /// </summary>
    public int FailedLoginMinimumMilliseconds { get; set; } = 250;

    // --- Refresh tokens ---
    /// <summary>
    /// Opt-in retry-race tolerance for refresh token rotation. When > 0, a consumed
    /// refresh token presented within this window of its consumption is treated as
    /// an idempotent retry: the successor tokens are re-delivered instead of
    /// triggering the replay-revoke policy. Set to 0 (default) to keep the strict
    /// "any reuse of a consumed token revokes everything" behaviour, which is the
    /// safer posture but causes user-visible logouts for clients that occasionally
    /// fire a second refresh with the same token (typically mobile apps with
    /// connectivity flaps). Matches Duende's ConsumedTokenUsageWindow concept.
    /// </summary>
    /// <summary>
    /// Window in which re-presenting an already-rotated refresh token returns its successor instead
    /// of being treated as replay. 0 (the default) means strict: any reuse revokes the grant family.
    /// </summary>
    /// <remarks>
    /// Left strict by default deliberately — it is the theft-detection behaviour, and weakening it
    /// for every deployment is not the right answer to one client topology. But a multi-instance BFF
    /// NEEDS a non-zero value: its refresh single-flight is per-process, so two replicas refreshing
    /// one session concurrently both redeem the same token, which strict mode reads as replay and
    /// answers by signing the user out everywhere. Set this (30 is the protocol layer's own default)
    /// when running more than one BFF instance, or any client that can refresh concurrently.
    /// </remarks>
    public int RefreshTokenReuseGraceSeconds { get; set; } = 0;

    // --- Signing keys ---
    public int SigningKeyLifetimeDays { get; set; } = 90;
    public int SigningKeyCacheRefreshMinutes { get; set; } = 60;

    // --- Key rotation ---
    public bool KeyRotationEnabled { get; set; }
    public int KeyRotationCheckIntervalMinutes { get; set; } = 360;
    public int KeyRotationLeadTimeDays { get; set; } = 14;

    // --- Cookie validation ---
    public int SecurityStampRevalidationMinutes { get; set; } = 30;

    // --- Dynamic client registration (RFC 7591) ---
    /// <summary>
    /// Enable the /connect/register endpoint. Off by default because open registration
    /// can be abused in multi-tenant deployments.
    /// </summary>
    public bool DynamicClientRegistrationEnabled { get; set; }

    /// <summary>
    /// Scopes an anonymous registrant may put in its own <c>AllowedScopes</c>, on top of the four
    /// OIDC built-ins (<c>openid</c>, <c>profile</c>, <c>email</c>, <c>offline_access</c>) which are
    /// always registrable. Empty — the default — means the built-ins and nothing else.
    /// </summary>
    /// <remarks>
    /// The only test used to be "does this scope exist in the store", so a registrant could declare
    /// every API scope the deployment had ever defined — <c>billing.write</c>, <c>orders.read</c> —
    /// simply because they existed. Role-gated scopes were later refused, but the documented default
    /// for <see cref="Core.Models.Scope.AllowedRoles"/> is empty ("every scope until an operator says
    /// otherwise"), so the ungated majority stayed self-assignable. The per-user gate still means an
    /// unentitled user is not granted them, but the client should not be able to declare them, and a
    /// user staring at a consent screen listing <c>billing.write</c> next to an unvetted client_name
    /// has been handed a decision nobody vetted. An allowlist inverts the default: registration
    /// reaches what an operator named, not what happens to exist.
    /// </remarks>
    public List<string> DynamicClientRegistrationScopes { get; set; } = [];
}
