namespace Authagonal.Protocol;

/// <summary>
/// Configuration for the Authagonal protocol surface. Hosts bind this from
/// configuration or configure it inline when calling <c>AddAuthagonalProtocol</c>.
/// </summary>
public sealed class AuthagonalProtocolOptions
{
    /// <summary>
    /// Authentication scheme the authorize endpoint will challenge when the caller
    /// is not authenticated. For cookie-based hosts this is the cookie scheme; for
    /// custom handlers (e.g. a share-link scheme) it's whatever scheme name the host
    /// registered.
    /// </summary>
    public string AuthenticationScheme { get; set; } = "Cookies";

    /// <summary>
    /// Allows <c>/connect/authorize</c>, <c>/connect/token</c>, <c>/connect/userinfo</c> and
    /// <c>/connect/par</c> to answer plaintext http requests. Default false: a non-https request to any
    /// of them is refused with <c>invalid_request</c>.
    /// </summary>
    /// <remarks>
    /// RFC 6749 §3.1 and §3.2 require TLS at the authorization and token endpoints, and the reason is not
    /// ceremonial — a plaintext exchange hands anyone on the path the authorization code, the client
    /// secret in the Basic header, and the access and refresh tokens that come back. Because this package
    /// does not own the host's pipeline, the requirement is enforced by an endpoint filter attached to
    /// those four routes, so it holds however the host composes its middleware and whether it maps the
    /// whole surface or one endpoint at a time.
    /// <para>
    /// The scheme is read after routing, so a host that terminates TLS at a proxy passes with this left
    /// alone provided it calls <c>UseForwardedHeaders</c> AND names that proxy in
    /// <c>KnownProxies</c>/<c>KnownNetworks</c> — an empty trust set means the framework honours
    /// <c>X-Forwarded-Proto</c> from every caller, which makes the scheme, and therefore this
    /// requirement, settable by anyone who can reach the host. Set this option only for a host that
    /// genuinely serves the protocol surface over http — a local development host, or a test server.
    /// </para>
    /// </remarks>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Enable the admin / discovery endpoints that aren't required for pure
    /// protocol use. Disabled by default — hosts that want a full server call
    /// the Authagonal.Server extensions instead.
    /// </summary>
    public bool EnableDiscoveryEndpoints { get; set; } = true;

    /// <summary>
    /// Static OIDC clients to seed on startup. If empty, clients must already be
    /// present in <c>IClientStore</c>. Hosts with a single embedded client
    /// (e.g. a single first-party client) typically seed from config.
    /// </summary>
    public List<OidcClientDescriptor> Clients { get; set; } = [];

    /// <summary>
    /// Static scopes to seed on startup. Standard OIDC scopes (<c>openid</c>,
    /// <c>profile</c>, <c>email</c>, <c>offline_access</c>) do not need to be
    /// listed — they're recognised inline.
    /// </summary>
    public List<OidcScopeDescriptor> Scopes { get; set; } = [];

    /// <summary>
    /// Lifetime of a freshly generated signing key, in days. Host-side rotation
    /// services should key their lead-time threshold against the same value.
    /// </summary>
    public int SigningKeyLifetimeDays { get; set; } = 90;

    /// <summary>
    /// How often <see cref="Services.ProtocolKeyManager"/> re-reads signing keys
    /// from storage to pick up externally rotated keys (cluster rotation, admin
    /// action). Acts as the eventual-consistency window for new keys.
    /// </summary>
    public int SigningKeyCacheRefreshMinutes { get; set; } = 5;

    /// <summary>
    /// Window, in seconds, during which reuse of a just-rotated refresh token is
    /// treated as an idempotent retry instead of a replay attack. Set to zero to
    /// disable the grace window (strictest posture — any reuse revokes the family).
    /// </summary>
    public int RefreshTokenReuseGraceSeconds { get; set; } = 30;

    /// <summary>
    /// How long a pending just-in-time approval (a delegated exchange parked on an
    /// ask-policy action) stays redeemable before the polling agent gets
    /// <c>expired_token</c>. Short by design: an approval authorizes one task-shaped
    /// request, not a standing grant.
    /// </summary>
    public int ApprovalLifetimeSeconds { get; set; } = 300;
}

public sealed class OidcClientDescriptor
{
    public required string ClientId { get; set; }
    public string DisplayName { get; set; } = "";

    /// <summary>Null/empty for public clients. Any non-empty value is hashed on seed.</summary>
    public string? ClientSecret { get; set; }

    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>Audience values asserted on issued access tokens. Empty falls back to ClientId.</summary>
    public List<string> Audiences { get; set; } = [];

    /// <summary>
    /// Permitted scopes beyond the OIDC baseline (<c>openid</c>, <c>profile</c>,
    /// <c>email</c>, <c>offline_access</c>). Standard scopes need not be listed.
    /// </summary>
    public List<string> AllowedScopes { get; set; } = [];

    public bool RequirePkce { get; set; } = true;
    public bool AllowRefreshToken { get; set; } = true;
    public bool RequireClientSecret { get; set; }
    public bool RequireConsent { get; set; }

    public int AccessTokenLifetimeSeconds { get; set; } = 900;
    public int IdentityTokenLifetimeSeconds { get; set; } = 900;
    public int AuthorizationCodeLifetimeSeconds { get; set; } = 300;
    public int AbsoluteRefreshTokenLifetimeSeconds { get; set; } = 60 * 60 * 24 * 7;
    public int SlidingRefreshTokenLifetimeSeconds { get; set; } = 60 * 60 * 24 * 7;
}

public sealed class OidcScopeDescriptor
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// A sentence explaining what granting this scope lets the application do, shown under the display
    /// name on the consent screen.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Marks the scope as consequential, so the consent screen can draw attention to it. Use it for the
    /// scopes that let an application change or remove things, which otherwise sit indistinguishably in
    /// a list of read permissions.
    /// </summary>
    public bool Emphasize { get; set; }

    /// <summary>
    /// A heading to file this scope under on the consent screen. Scopes sharing a group are shown
    /// together under it; a scope with no group stands on its own.
    /// </summary>
    /// <remarks>
    /// Purely how the screen is arranged — grouping changes nothing about what is requested or granted.
    /// </remarks>
    public string? Group { get; set; }

    /// <summary>
    /// The user may not decline this scope: consent shows it ticked and locked. Reserve it for scopes
    /// the application genuinely cannot work without — a required scope is a choice taken away.
    /// </summary>
    public bool Required { get; set; }

    public bool ShowInDiscoveryDocument { get; set; } = true;

    /// <summary>
    /// Claim names this scope releases from <see cref="OidcSubject.CustomAttributes"/>
    /// onto issued access tokens. Standard OIDC claims (email, profile fields,
    /// <c>org_id</c>, <c>roles</c>, <c>groups</c>) are handled inline and need not
    /// be listed.
    /// </summary>
    public List<string> UserClaims { get; set; } = [];
}
