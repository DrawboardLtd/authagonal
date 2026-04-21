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
    /// Enable the admin / discovery endpoints that aren't required for pure
    /// protocol use. Disabled by default — hosts that want a full server call
    /// the Authagonal.Server extensions instead.
    /// </summary>
    public bool EnableDiscoveryEndpoints { get; set; } = true;

    /// <summary>
    /// Static OIDC clients to seed on startup. If empty, clients must already be
    /// present in <c>IClientStore</c>. Hosts with a single embedded client
    /// (e.g. bullclip's one Authagonal Cloud client) typically seed from config.
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
    public bool ShowInDiscoveryDocument { get; set; } = true;

    /// <summary>
    /// Claim names this scope releases from <see cref="OidcSubject.CustomAttributes"/>
    /// onto issued access tokens. Standard OIDC claims (email, profile fields,
    /// <c>org_id</c>, <c>roles</c>, <c>groups</c>) are handled inline and need not
    /// be listed.
    /// </summary>
    public List<string> UserClaims { get; set; } = [];
}
