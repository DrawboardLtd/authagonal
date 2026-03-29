namespace Authagonal.Core.Models;

public sealed class OAuthClient
{
    public required string ClientId { get; set; }
    public required string ClientName { get; set; }
    public List<string> ClientSecretHashes { get; set; } = [];
    public List<string> AllowedGrantTypes { get; set; } = [];
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = [];
    public List<string> AllowedCorsOrigins { get; set; } = [];
    public bool RequirePkce { get; set; } = true;
    public bool AllowOfflineAccess { get; set; }
    public bool RequireClientSecret { get; set; } = true;
    public bool AlwaysIncludeUserClaimsInIdToken { get; set; }
    public int AccessTokenLifetimeSeconds { get; set; } = 1800;
    public int IdentityTokenLifetimeSeconds { get; set; } = 300;
    public int AuthorizationCodeLifetimeSeconds { get; set; } = 300;
    public int AbsoluteRefreshTokenLifetimeSeconds { get; set; } = 2592000;
    public int SlidingRefreshTokenLifetimeSeconds { get; set; } = 1296000;
    public RefreshTokenUsage RefreshTokenUsage { get; set; } = RefreshTokenUsage.OneTime;
    public List<string> ProvisioningApps { get; set; } = [];
    public MfaPolicy MfaPolicy { get; set; } = MfaPolicy.Disabled;
}

public enum RefreshTokenUsage
{
    ReUse = 0,
    OneTime = 1
}

public enum MfaPolicy
{
    Disabled = 0,
    Enabled = 1,
    Required = 2
}
