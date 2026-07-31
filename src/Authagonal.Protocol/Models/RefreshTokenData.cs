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

    /// <summary>
    /// The still-live access tokens minted under this refresh family, so revoking the refresh token
    /// can revoke them too (RFC 7009 §2.1).
    /// </summary>
    /// <remarks>
    /// Access tokens are self-contained ES256 JWTs — there is no reference-token mode — so the only
    /// channel that can kill one is <see cref="IRevokedTokenStore"/>, keyed by <c>jti</c>. Nothing
    /// recorded the jti anywhere, so the capability could not be invoked: revoking a refresh token
    /// left its access token valid for the remainder of <c>AccessTokenLifetimeSeconds</c> (default
    /// 1800s), including on the server's own replay-detection path, where the thief's access token
    /// is the one nobody can name.
    /// <para>
    /// Bounded two ways so this cannot grow with the family's 30-day absolute life: entries whose
    /// <see cref="IssuedAccessToken.ExpiresAt"/> has passed are dropped on every write (an expired
    /// jti needs no revocation — the token is already dead), and the survivors are hard-capped at
    /// <see cref="MaxTrackedAccessTokens"/> newest-first. In steady state the list holds one or two
    /// entries, because rotation happens on roughly the access-token lifetime.
    /// </para>
    /// </remarks>
    public List<IssuedAccessToken>? AccessTokens { get; set; }

    /// <summary>
    /// Ceiling on <see cref="AccessTokens"/>. Only reachable if a client refreshes far faster than
    /// its access tokens expire; dropping the oldest is the right loss, since a token that survived
    /// that many rotations is nearest its own expiry.
    /// </summary>
    internal const int MaxTrackedAccessTokens = 50;
}

/// <summary>An access token minted under a refresh family, recorded so it can be revoked with it.</summary>
internal sealed class IssuedAccessToken
{
    /// <summary>The token's <c>jti</c> claim — the key <see cref="IRevokedTokenStore"/> is keyed by.</summary>
    public required string Jti { get; set; }

    /// <summary>Natural expiry, so expired entries can be pruned and the revocation entry can be
    /// written with the token's own TTL rather than an invented one.</summary>
    public required DateTimeOffset ExpiresAt { get; set; }
}
