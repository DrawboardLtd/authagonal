namespace Authagonal.Server.Services;

/// <summary>
/// Configuration for authentication, rate limiting, and token expiry settings.
/// Bound from the "Auth" configuration section.
/// </summary>
public sealed class AuthOptions
{
    // --- Account lockout ---
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 10;

    // --- Registration rate limiting ---
    public int MaxRegistrationsPerIp { get; set; } = 5;
    public int RegistrationWindowMinutes { get; set; } = 60;

    /// <summary>
    /// When true, registering an email that belongs to an existing account WITH NO LOCAL CREDENTIAL
    /// (a federated / JIT-provisioned account, PasswordHash null) sets the password on that account
    /// and runs provisioning, rather than returning the enumeration-neutral duplicate response. This
    /// lets a federated-only account claim a first-party credential through the normal register flow;
    /// what provisioning does with it is the downstream app's concern. An account that ALREADY has a
    /// password is never affected — a re-register can never overwrite a real credential. Default OFF:
    /// a generic deployment treats every existing email as a duplicate.
    /// </summary>
    public bool AllowPasswordlessAccountClaim { get; set; }

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

    // --- Password hashing ---
    public int Pbkdf2Iterations { get; set; } = 100_000;

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
}
