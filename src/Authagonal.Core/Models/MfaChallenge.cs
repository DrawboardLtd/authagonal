namespace Authagonal.Core.Models;

public sealed class MfaChallenge
{
    public required string ChallengeId { get; set; }
    public required string UserId { get; set; }
    public string? ClientId { get; set; }
    public string? ReturnUrl { get; set; }

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
