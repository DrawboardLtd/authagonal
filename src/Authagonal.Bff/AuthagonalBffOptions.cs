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
    /// discovered from <c>{Authority}/.well-known/openid-configuration</c>. Required in single-tenant mode;
    /// ignored (a custom <see cref="IBffTenantResolver"/> supplies it per tenant) when
    /// <see cref="TenantQueryParam"/> is set.</summary>
    public string Authority { get; set; } = default!;

    /// <summary>The confidential client id registered in Authagonal for this BFF. Required in single-tenant
    /// mode; supplied per tenant by the resolver in multi-tenant mode.</summary>
    public string ClientId { get; set; } = default!;

    /// <summary>The client secret. The BFF is a confidential client; this must be set in single-tenant mode
    /// (supplied per tenant by the resolver in multi-tenant mode).</summary>
    public string ClientSecret { get; set; } = default!;

    /// <summary>Multi-tenant switch. When set, one BFF serves many tenants: <c>/bff/login</c> reads the tenant
    /// key from this query parameter (e.g. <c>"slug"</c> ⇒ <c>/bff/login?slug=acme</c>), a registered
    /// <see cref="IBffTenantResolver"/> resolves the per-tenant <see cref="BffTenantConfig"/>, and the key is
    /// persisted on the session so later requests re-resolve it. When null (the default) the BFF is
    /// single-tenant and <see cref="Authority"/>/<see cref="ClientId"/>/<see cref="ClientSecret"/> are used.</summary>
    public string? TenantQueryParam { get; set; }

    /// <summary>True when the BFF is configured to serve many tenants (<see cref="TenantQueryParam"/> is set).</summary>
    public bool IsMultiTenant => !string.IsNullOrWhiteSpace(TenantQueryParam);

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

    /// <summary>Query-parameter names copied verbatim from <c>{BasePath}/login</c> through to the upstream
    /// <c>/authorize</c> request, when present on the login request. Lets a consumer drive IdP-federation
    /// params the OIDC client itself doesn't model — e.g. <c>idp_hint</c> to select a federated upstream
    /// connection at the auth host, plus a share-link token that connection forwards downstream. Empty by
    /// default (only the standard OIDC params are sent).</summary>
    public IList<string> LoginPassthroughParams { get; set; } = new List<string>();

    /// <summary>Where Authagonal sends the browser after a completed logout.</summary>
    public string? PostLogoutRedirectUri { get; set; }

    /// <summary>Header the SPA must send on every non-navigation call (CSRF defence). Default
    /// <c>X-Authagonal-Bff</c>; any value is accepted, only presence is checked.</summary>
    public string AntiForgeryHeader { get; set; } = "X-Authagonal-Bff";

    /// <summary>Maximum lifetime of a BFF session regardless of token refreshes.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Enables <c>GET {BasePath}/ws-ticket</c>: mints a short-lived, single-use ticket the SPA can
    /// put on a websocket connect URL (a websocket handshake cannot carry custom headers or a bearer). The
    /// API host exchanges the ticket for the session's access token via the SHARED distributed cache — the
    /// in-memory cache default can never serve another process, so this requires Redis (or equivalent) to
    /// work across hosts. Off by default.</summary>
    public bool WsTicketsEnabled { get; set; }

    /// <summary>Websocket ticket lifetime. Kept short: the SPA mints a ticket immediately before each
    /// connect, and the ticket is deleted on first use.</summary>
    public TimeSpan WsTicketLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, a proxy request (<c>{BasePath}/api/**</c>) with no session — or a session that can no
    /// longer be refreshed — is forwarded to the upstream WITHOUT an <c>Authorization</c> header instead
    /// of being rejected with 401. This reproduces classic SPA semantics where anonymous calls reach the
    /// API and IT decides (e.g. [AllowAnonymous] share-link peeks work signed-out, protected endpoints
    /// return their own 401). The anti-forgery header is still required. Default false: only sessions
    /// pass the proxy.
    /// </summary>
    public bool AllowAnonymousProxyRequests { get; set; }

    /// <summary>Upstream APIs the proxy (<c>{BasePath}/api/**</c>) forwards to with the session's access
    /// token attached. Empty (the default) disables the proxy endpoint.</summary>
    public IList<BffUpstream> Upstreams { get; set; } = new List<BffUpstream>();

    /// <summary>
    /// Query parameters <c>{BasePath}/ws-ticket</c> may forward into an RFC 8693 token exchange
    /// (e.g. <c>["project_id","workspace_id"]</c>). When a ticket request carries any of them, the
    /// ticket is bound to the EXCHANGED context token instead of the session's primary access
    /// token; a denied exchange fails the mint with 403. Empty (default) = tickets always carry
    /// the primary token. Requires the tenant client to hold the token-exchange grant.
    /// </summary>
    public IList<string> TicketExchangeParams { get; set; } = new List<string>();

    /// <summary>
    /// Proxy routes whose upstream calls ride a context-bound exchanged token instead of the
    /// session's primary access token. The first pattern matching the proxied path (after
    /// <c>{BasePath}/api</c>) wins; the captured segment is sent as the named exchange parameter
    /// and the downscoped result is cached per (session, binding) for its lifetime. A denied
    /// exchange → 403. Empty (default) = the proxy always attaches the primary token.
    /// </summary>
    public IList<BffExchangeRoute> ExchangeRoutes { get; set; } = new List<BffExchangeRoute>();

    internal string ScopeString => string.Join(' ', Scope);

    internal string CorrelationCookieName => CookieName + ".tmp";

    internal void Validate()
    {
        // In multi-tenant mode a registered IBffTenantResolver supplies Authority/ClientId/ClientSecret per
        // tenant, so the static single-tenant fields are not required (and are ignored if set).
        if (!IsMultiTenant)
        {
            if (string.IsNullOrWhiteSpace(Authority))
                throw new InvalidOperationException("AuthagonalBffOptions.Authority is required (or set TenantQueryParam for multi-tenant mode).");
            if (string.IsNullOrWhiteSpace(ClientId))
                throw new InvalidOperationException("AuthagonalBffOptions.ClientId is required.");
            if (string.IsNullOrWhiteSpace(ClientSecret))
                throw new InvalidOperationException("AuthagonalBffOptions.ClientSecret is required (the BFF is a confidential client).");
        }
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

    /// <summary>When true, the matched <see cref="Prefix"/> is stripped from the path before it is appended
    /// to <see cref="TargetBaseUrl"/>. Use a synthetic routing prefix to fan one BFF out to several backends
    /// that share a path namespace: e.g. <c>/id</c> stripping ⇒ <c>/bff/api/id/api/admin/x</c> forwards to
    /// <c>{TargetBaseUrl}/api/admin/x</c>. Default false (the prefix is a real path segment on the target).</summary>
    public bool StripPrefix { get; set; }
}

/// <summary>A proxy route bound to an RFC 8693 exchange. <see cref="PathPattern"/> is a segment
/// pattern matched as a prefix against the proxied path (after <c>{BasePath}/api</c>), with exactly
/// one <c>{param}</c> placeholder whose captured segment becomes the exchange parameter — e.g.
/// <c>/projects/{project_id}</c> matches <c>/projects/123/annotations</c> and sends
/// <c>project_id=123</c> on the exchange.</summary>
public sealed class BffExchangeRoute
{
    public string PathPattern { get; set; } = default!;
}
