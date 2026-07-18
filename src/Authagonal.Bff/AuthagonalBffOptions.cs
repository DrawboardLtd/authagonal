namespace Authagonal.Bff;

/// <summary>How the BFF stores its sessions.</summary>
public enum BffSessionMode
{
    /// <summary>Tokens live server-side; the cookie carries only an opaque session id. Required for
    /// back-channel logout (a remote session must be findable to be killed).</summary>
    Store,

    /// <summary>Reserved. Tokens live in an encrypted cookie with no server-side store. Not yet
    /// implemented; cannot support remote (back-channel) logout.</summary>
    Stateless,
}

/// <summary>Configuration for the Authagonal BFF middleware.</summary>
public sealed class AuthagonalBffOptions
{
    /// <summary>The tenant auth host, e.g. <c>https://acme-admin.authagonal.io</c>. OIDC metadata is
    /// discovered from <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string Authority { get; set; } = default!;

    /// <summary>The confidential client id registered in Authagonal for this BFF.</summary>
    public string ClientId { get; set; } = default!;

    /// <summary>The client secret. The BFF is a confidential client; this must be set.</summary>
    public string ClientSecret { get; set; } = default!;

    /// <summary>Requested scopes. Include <c>offline_access</c> to enable server-side refresh.</summary>
    public IList<string> Scope { get; set; } = new List<string> { "openid", "profile", "offline_access" };

    /// <summary>Base path the BFF endpoints are mounted under. Default <c>/bff</c>.</summary>
    public string BasePath { get; set; } = "/bff";

    /// <summary>Absolute path of the OIDC redirect URI. Must match the client's registered redirect URI.
    /// Default <c>/bff/callback</c>.</summary>
    public string CallbackPath { get; set; } = "/bff/callback";

    /// <summary>Session cookie name. The <c>__Host-</c> prefix forces Secure + Path=/ + no Domain, so it
    /// requires HTTPS. Override for local HTTP development.</summary>
    public string CookieName { get; set; } = "__Host-agbff";

    /// <summary>Where sessions are stored. Only <see cref="BffSessionMode.Store"/> is implemented.</summary>
    public BffSessionMode SessionMode { get; set; } = BffSessionMode.Store;

    /// <summary>Refresh the access token when it is within this many seconds of expiry.</summary>
    public int RefreshThresholdSeconds { get; set; } = 60;

    /// <summary>Absolute origins (scheme://host[:port]) that a non-relative <c>returnUrl</c> may target.
    /// Relative paths are always allowed; everything else is coerced to <c>/</c>.</summary>
    public IList<string> ReturnUrlAllowlist { get; set; } = new List<string>();

    /// <summary>Where Authagonal sends the browser after a completed logout.</summary>
    public string? PostLogoutRedirectUri { get; set; }

    /// <summary>Header the SPA must send on every non-navigation call (CSRF defence). Default
    /// <c>X-Authagonal-Bff</c>; any value is accepted, only presence is checked.</summary>
    public string AntiForgeryHeader { get; set; } = "X-Authagonal-Bff";

    /// <summary>Maximum lifetime of a BFF session regardless of token refreshes.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Upstream APIs the proxy (<c>{BasePath}/api/**</c>) forwards to with the session's access
    /// token attached. Empty (the default) disables the proxy endpoint.</summary>
    public IList<BffUpstream> Upstreams { get; set; } = new List<BffUpstream>();

    internal string ScopeString => string.Join(' ', Scope);

    internal string CorrelationCookieName => CookieName + ".tmp";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Authority))
            throw new InvalidOperationException("AuthagonalBffOptions.Authority is required.");
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException("AuthagonalBffOptions.ClientId is required.");
        if (string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("AuthagonalBffOptions.ClientSecret is required (the BFF is a confidential client).");
        if (SessionMode == BffSessionMode.Stateless)
            throw new InvalidOperationException("BffSessionMode.Stateless is not implemented yet; use BffSessionMode.Store.");

        BasePath = "/" + BasePath.Trim('/');
        CallbackPath = "/" + CallbackPath.Trim('/');
    }
}

/// <summary>An upstream API the BFF proxy forwards to. The path after <c>{BasePath}/api</c> is matched
/// against <see cref="Prefix"/> to select the upstream, then appended to <see cref="TargetBaseUrl"/>.</summary>
public sealed class BffUpstream
{
    /// <summary>Path prefix (after <c>{BasePath}/api</c>) this upstream handles, e.g. <c>/orders</c>.</summary>
    public string Prefix { get; set; } = "/";

    /// <summary>Base URL requests are forwarded to, e.g. <c>https://api.internal.acme.com</c>.</summary>
    public string TargetBaseUrl { get; set; } = default!;
}
