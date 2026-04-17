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

    // --- Token / link expiry ---
    public int EmailVerificationExpiryHours { get; set; } = 24;
    public int PasswordResetExpiryMinutes { get; set; } = 60;
    public int MfaChallengeExpiryMinutes { get; set; } = 5;
    public int MfaSetupTokenExpiryMinutes { get; set; } = 15;

    // --- Password hashing ---
    public int Pbkdf2Iterations { get; set; } = 100_000;

    // --- Refresh tokens ---
    public int RefreshTokenReuseGraceSeconds { get; set; } = 10;

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
