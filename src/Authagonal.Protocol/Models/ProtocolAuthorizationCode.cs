namespace Authagonal.Protocol.Models;

/// <summary>
/// Persisted authorization code payload. Stored in <c>PersistedGrant.Data</c> as JSON and
/// reloaded at token exchange. Carries the full <see cref="OidcSubject"/> captured at
/// authorize time so the token endpoint can mint tokens without re-calling the subject
/// resolver — the resolver is only re-engaged on refresh.
/// </summary>
internal sealed class ProtocolAuthorizationCode
{
    public required string Code { get; set; }
    public required string ClientId { get; set; }
    public required string SubjectId { get; set; }
    public required string RedirectUri { get; set; }
    public required List<string> Scopes { get; set; }
    public List<string>? Resources { get; set; }
    public string? CodeChallenge { get; set; }
    public string? CodeChallengeMethod { get; set; }
    public string? Nonce { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? SessionMaxExpiresAt { get; set; }

    public required OidcSubject Subject { get; set; }
}
