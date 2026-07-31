namespace Authagonal.Bff;

/// <summary>A server-side BFF session. Persisted via <see cref="IBffSessionStore"/>; the browser only
/// ever sees <see cref="SessionId"/> (in an httpOnly cookie), never the tokens.</summary>
public sealed class BffSession
{
    /// <summary>Opaque, unguessable session id. Equals the value stored in the session cookie.</summary>
    public string SessionId { get; set; } = default!;

    /// <summary>The tenant this session belongs to (see <see cref="IBffTenantResolver"/>). Null in single-tenant
    /// mode. Re-resolved to the tenant's client config on every refresh / logout.</summary>
    public string? TenantKey { get; set; }

    /// <summary>The OIDC session id (<c>sid</c>) from the id_token, used to match back-channel logout
    /// tokens to this session. Null if the provider didn't issue one.</summary>
    public string? Sid { get; set; }

    /// <summary>The authenticated subject (<c>sub</c>).</summary>
    public string Subject { get; set; } = default!;

    /// <summary>The raw id_token, retained for use as <c>id_token_hint</c> on logout.</summary>
    public string IdToken { get; set; } = default!;

    /// <summary>The current access token (sent to downstream APIs by the proxy; never to the browser).</summary>
    public string AccessToken { get; set; } = default!;

    /// <summary>The current refresh token, if <c>offline_access</c> was granted.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>When the access token expires.</summary>
    public DateTimeOffset AccessTokenExpiresAt { get; set; }

    /// <summary>Absolute session expiry, independent of token refreshes.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the session was established. Set once by the store; a token refresh does not move it.
    /// </summary>
    /// <remarks>
    /// Compared against the subject/sid revocation markers on every load, so a back-channel logout
    /// terminates a session whether or not the secondary index happened to record it. Sessions
    /// serialized before this field existed read as <c>default</c>, which is before any marker and
    /// therefore revoked by one — the safe direction.
    /// </remarks>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Non-sensitive id_token claims surfaced to the SPA via <c>/bff/user</c>.</summary>
    public Dictionary<string, string> Claims { get; set; } = new();
}
