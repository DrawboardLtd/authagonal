namespace Authagonal.OidcProvider;

public sealed class AuthagonalOidcProviderOptions
{
    /// <summary>
    /// The OIDC issuer (e.g. <c>https://auth.example.com</c>). Required. Used as the
    /// <c>iss</c> claim and advertised at <c>/.well-known/openid-configuration</c>.
    /// </summary>
    public string? Issuer { get; set; }

    public string AuthorizationEndpointPath { get; set; } = "/connect/authorize";
    public string TokenEndpointPath { get; set; } = "/connect/token";
    public string UserinfoEndpointPath { get; set; } = "/connect/userinfo";
    public string JwksEndpointPath { get; set; } = "/.well-known/jwks";

    /// <summary>
    /// Authentication scheme used to identify the logged-in user at the authorize
    /// endpoint. Typically the host's cookie auth scheme
    /// (e.g. <c>CookieAuthenticationDefaults.AuthenticationScheme</c>).
    /// </summary>
    public string AuthenticationScheme { get; set; } = "Cookies";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan IdentityTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan AuthorizationCodeLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    public List<OidcClientDescriptor> Clients { get; set; } = [];
}

public sealed class OidcClientDescriptor
{
    public required string ClientId { get; set; }

    /// <summary>Null for public clients. Hashed server-side by OpenIddict on registration.</summary>
    public string? ClientSecret { get; set; }

    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>
    /// Permitted scopes beyond the OIDC baseline (<c>openid</c>, <c>profile</c>,
    /// <c>email</c>, <c>offline_access</c>).
    /// </summary>
    public List<string> AllowedScopes { get; set; } = [];

    public bool RequirePkce { get; set; } = true;
    public bool AllowRefreshToken { get; set; } = true;

    public string DisplayName { get; set; } = "";
}
