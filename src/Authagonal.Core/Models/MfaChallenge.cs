namespace Authagonal.Core.Models;

/// <summary>
/// What a challenge entitles its bearer to do. A challenge id is a bearer credential handed to a
/// caller who has proved a password but has no session yet, and the three cases below carry very
/// different authority — so the purpose is recorded on the record and checked at every consumer
/// rather than inferred from which endpoint minted it.
/// </summary>
public enum MfaChallengePurpose
{
    /// <summary>Prove an existing second factor. Entitles the bearer to nothing except
    /// <c>/api/auth/mfa/verify</c>. This is deliberately value 0 so a row written before this field
    /// existed deserializes as the least-privileged case: an in-flight enrolment token from a prior
    /// deployment is refused (the user restarts enrolment) rather than silently keeping setup power.</summary>
    Verify = 0,

    /// <summary>Enrol a FIRST factor, for a user who has none. The only purpose the
    /// <c>/api/auth/mfa/*</c> setup endpoints accept via <c>X-MFA-Setup-Token</c>.</summary>
    Enrol = 1,

    /// <summary>Carries a WebAuthn assertion challenge for passwordless discovery, before any user is
    /// known. <see cref="MfaChallenge.UserId"/> is empty, so it identifies nobody and must never be
    /// accepted as proof of identity.</summary>
    PasswordlessDiscovery = 2,
}

public sealed class MfaChallenge
{
    public required string ChallengeId { get; set; }
    public required string UserId { get; set; }
    public string? ClientId { get; set; }
    public string? ReturnUrl { get; set; }

    /// <summary>What the bearer of this challenge id may do. See <see cref="MfaChallengePurpose"/>.</summary>
    public MfaChallengePurpose Purpose { get; set; }

    /// <summary>Base64-encoded challenge bytes for WebAuthn assertion verification.</summary>
    public string? WebAuthnChallenge { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsConsumed { get; set; }

    /// <summary>Failed verify attempts against this challenge. The verify endpoint validates the code
    /// BEFORE consuming, so a wrong code no longer burns the challenge — it increments this instead,
    /// and the challenge is consumed (forcing a fresh login) only once a bounded budget is exhausted.
    /// Bounds TOTP brute-force while allowing an honest retry after a mistyped digit.</summary>
    public int Attempts { get; set; }
}
