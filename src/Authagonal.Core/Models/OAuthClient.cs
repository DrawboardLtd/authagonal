namespace Authagonal.Core.Models;

public sealed record OAuthClient
{
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string? Description { get; set; }
    public string? ClientUri { get; set; }
    public string? LogoUri { get; set; }
    /// <summary>RP endpoint that starts a fresh login when visited (OIDC third-party initiated
    /// login). Preferred over <see cref="ClientUri"/> as the "back to app" target because the RP
    /// originates a proper authorize flow instead of relying on its middleware.</summary>
    public string? InitiateLoginUri { get; set; }
    /// <summary>Marks this client as the tenant's default application: the "back to app" target
    /// when a user reaches the hosted account pages with no flow context. At most one client may
    /// hold the flag (the admin API clears it from others on write); when no client holds it and
    /// exactly one client has a home URI, that client is the implicit default.</summary>
    public bool IsDefaultApplication { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> ClientSecretHashes { get; set; } = [];
    public List<string> AllowedGrantTypes { get; set; } = [];
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public string? BackChannelLogoutUri { get; set; }
    public bool BackChannelLogoutSessionRequired { get; set; } = true;
    public string? FrontChannelLogoutUri { get; set; }
    public bool FrontChannelLogoutSessionRequired { get; set; } = true;
    public List<string> Audiences { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = [];
    public List<string> AllowedCorsOrigins { get; set; } = [];
    public bool RequirePkce { get; set; } = true;
    public bool AllowOfflineAccess { get; set; }
    public bool RequireClientSecret { get; set; } = true;
    public bool AlwaysIncludeUserClaimsInIdToken { get; set; }
    public bool IncludeGroupsInTokens { get; set; }
    public int AccessTokenLifetimeSeconds { get; set; } = 1800;
    public int IdentityTokenLifetimeSeconds { get; set; } = 300;
    public int AuthorizationCodeLifetimeSeconds { get; set; } = 300;
    public int AbsoluteRefreshTokenLifetimeSeconds { get; set; } = 2592000;
    public int SlidingRefreshTokenLifetimeSeconds { get; set; } = 1296000;
    public int DeviceCodeLifetimeSeconds { get; set; } = 300;
    public bool RequirePushedAuthorizationRequests { get; set; }
    public RefreshTokenUsage RefreshTokenUsage { get; set; } = RefreshTokenUsage.OneTime;
    public RefreshTokenExpiration RefreshTokenExpiration { get; set; } = RefreshTokenExpiration.Absolute;
    public bool RequireConsent { get; set; }
    public List<string> ProvisioningApps { get; set; } = [];
    public MfaPolicy MfaPolicy { get; set; } = MfaPolicy.Disabled;

    /// <summary>Inline JWKS document (RFC 7517) holding the client's public signing keys.
    /// Setting this (or <see cref="JwksUri"/>) enables <c>private_key_jwt</c> client
    /// authentication (RFC 7523) — the right credential for agent workloads, where a shared
    /// secret is the weakest link in the delegation chain.</summary>
    public string? JwksJson { get; set; }

    /// <summary>URL the client's JWKS is fetched from (cached ~10 minutes). Alternative to
    /// <see cref="JwksJson"/> for clients that rotate keys; ignored when JwksJson is set.</summary>
    public string? JwksUri { get; set; }
}

public enum RefreshTokenUsage
{
    ReUse = 0,
    OneTime = 1
}

public enum RefreshTokenExpiration
{
    // Hard cap — every rotation keeps the original issuance window; the refresh token
    // expires at OriginalCreatedAt + AbsoluteRefreshTokenLifetime regardless of activity.
    Absolute = 0,
    // Window extends by SlidingRefreshTokenLifetime on each rotation, capped at
    // OriginalCreatedAt + AbsoluteRefreshTokenLifetime.
    Sliding = 1,
}

public enum MfaPolicy
{
    Disabled = 0,
    Enabled = 1,
    Required = 2
}
