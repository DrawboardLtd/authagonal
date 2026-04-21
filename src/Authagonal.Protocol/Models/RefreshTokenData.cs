namespace Authagonal.Protocol.Models;

/// <summary>
/// Internal model for refresh token data serialized in <c>PersistedGrant.Data</c>.
/// Carries the prior <see cref="OidcSubject"/> so the host's <see cref="IOidcSubjectResolver"/>
/// can re-validate the session on refresh without any coupling to the identity store.
/// </summary>
internal sealed class RefreshTokenData
{
    public required List<string> Scopes { get; set; }
    public List<string>? Resources { get; set; }
    public required string SubjectId { get; set; }
    public required string ClientId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>First-issuance timestamp so the absolute refresh lifetime cap survives rotations.</summary>
    public DateTimeOffset? OriginalCreatedAt { get; set; }

    /// <summary>Handle of the successor grant, set at rotation so a retry inside the grace window can be served idempotently.</summary>
    public string? SuccessorKey { get; set; }

    /// <summary>Upstream session cap (e.g. federated IdP max session). Preserved across rotations so refresh cannot lift the cap.</summary>
    public DateTimeOffset? SessionMaxExpiresAt { get; set; }

    /// <summary>Prior subject captured at authorize. Passed to <see cref="IOidcSubjectResolver.ResolveRefreshAsync"/> on each refresh.</summary>
    public required OidcSubject Subject { get; set; }
}
