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
}
