namespace Authagonal.Bff;

/// <summary>The resolved OIDC client configuration the BFF runs a single request against. In single-tenant
/// mode this is just the configured <see cref="AuthagonalBffOptions"/> (see <see cref="StaticBffTenantResolver"/>);
/// in multi-tenant mode a custom <see cref="IBffTenantResolver"/> returns a different one per tenant — e.g. a
/// per-tenant authority derived from a slug, with a shared confidential client.</summary>
public sealed record BffTenantConfig
{
    /// <summary>Opaque key identifying the tenant within this BFF (e.g. a slug). Persisted on the session and
    /// used to re-resolve this config on later requests. Null in single-tenant mode.</summary>
    public string? TenantKey { get; init; }

    /// <summary>The tenant auth host, e.g. <c>https://acme-admin.authagonal.io</c>. OIDC metadata is discovered
    /// from <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public required string Authority { get; init; }

    /// <summary>The confidential client id registered in this tenant for the BFF.</summary>
    public required string ClientId { get; init; }

    /// <summary>The client secret. The BFF is a confidential client; this must be set.</summary>
    public required string ClientSecret { get; init; }

    /// <summary>Requested scopes. Include <c>offline_access</c> to enable server-side refresh.</summary>
    public required IReadOnlyList<string> Scope { get; init; }

    internal string ScopeString => string.Join(' ', Scope);
}

/// <summary>Resolves the per-tenant OIDC client configuration a BFF request runs against. The default
/// (<see cref="StaticBffTenantResolver"/>) always returns the single tenant configured in
/// <see cref="AuthagonalBffOptions"/>, so single-tenant hosts and every existing consumer are unaffected.
/// Register a custom implementation (<c>services.AddSingleton&lt;IBffTenantResolver, T&gt;()</c>) to serve many
/// tenants from one BFF, and set <see cref="AuthagonalBffOptions.TenantQueryParam"/> so <c>/bff/login</c> knows
/// which query parameter carries the tenant key.</summary>
public interface IBffTenantResolver
{
    /// <summary>Resolve by tenant key — the value of <see cref="AuthagonalBffOptions.TenantQueryParam"/> at login
    /// (null in single-tenant mode), then the key persisted on the session for every later request. Returns null
    /// if the key is unknown or invalid (login is then rejected).</summary>
    Task<BffTenantConfig?> ResolveAsync(string? tenantKey, CancellationToken ct = default);

    /// <summary>Resolve by OIDC issuer, for back-channel logout — which carries no session cookie, only a signed
    /// token whose <c>iss</c> identifies the tenant. Returns null if the issuer is not one this BFF serves.</summary>
    Task<BffTenantConfig?> ResolveByIssuerAsync(string issuer, CancellationToken ct = default);
}
