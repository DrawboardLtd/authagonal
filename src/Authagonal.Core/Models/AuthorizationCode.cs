namespace Authagonal.Core.Models;

public sealed class AuthorizationCode
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

    /// <summary>
    /// Upper bound on how long tokens issued from this code may live, derived from a
    /// session_max_exp cookie claim set during upstream federation (e.g. an SSO IdP that
    /// caps subject sessions). Preserved through rotation so refresh tokens cannot outlive
    /// the original federated session.
    /// </summary>
    public DateTimeOffset? SessionMaxExpiresAt { get; set; }
}
