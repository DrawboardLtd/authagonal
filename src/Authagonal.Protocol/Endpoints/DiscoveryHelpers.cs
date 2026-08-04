using Authagonal.Core.Authority;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// Document-building pieces shared by the Protocol and Server discovery/JWKS endpoints.
/// </summary>
internal static class DiscoveryHelpers
{
    /// <summary>
    /// The claims this OP can put in an ID token or return from userinfo.
    /// </summary>
    /// <remarks>
    /// One list, because there were two hand-maintained copies — one per host — and they had drifted: the
    /// Protocol host's ended <c>"groups", "org_id"</c> and the Server host's ended <c>"groups"</c>. The Server
    /// is the host that emits <c>org_id</c> most explicitly (its userinfo returns it under <c>profile</c>, and
    /// <c>ProtocolTokenService</c> lists it among the reserved first-class claims and calls it the claim that
    /// "decides tenancy"), so the copy that omitted it was the one that most needed it. An RP that builds its
    /// claim mapping from <c>claims_supported</c> built no tenancy mapping, and a policy engine validating
    /// received claims against the advertised set saw <c>org_id</c> as unexpected.
    /// </remarks>
    public static readonly string[] ClaimsSupported =
    [
        "sub", "iss", "aud", "exp", "iat", "auth_time",
        "email", "email_verified",
        "name", "given_name", "family_name",
        "phone_number",
        "roles", "groups",
        "org_id",
    ];

    /// <summary>
    /// The paths discovery metadata is served at. OIDC discovery is a superset of RFC 8414's
    /// authorization-server metadata, so one document satisfies both.
    /// </summary>
    /// <remarks>
    /// Shared for the same reason as <see cref="ClaimsSupported"/>: the Server mapped both paths and the
    /// Protocol package mapped only the OIDC one. The MCP authorization spec has clients resolve the
    /// authorization server via <c>oauth-authorization-server</c> FIRST and is not obliged to fall back to
    /// OIDC discovery — and the Protocol package is precisely the one documented for embedding OAuth in an
    /// existing app, including for MCP servers. So the host that most needed the RFC 8414 path was the one
    /// that did not publish it.
    /// </remarks>
    public static readonly string[] MetadataPaths =
        ["/.well-known/openid-configuration", "/.well-known/oauth-authorization-server"];

    /// <summary>
    /// Built-in scopes plus any custom scopes flagged for the discovery document.
    /// Falls back to the built-ins if the scope store is unavailable.
    /// </summary>
    public static async Task<string[]> ResolveSupportedScopesAsync(IScopeStore scopeStore, CancellationToken ct)
    {
        // `phone`, `roles` and `groups` are advertised because the claims they gate are ones this OP
        // actually releases. Before, `phone_number` rode `profile` and `roles`/`groups` rode nothing,
        // so `claims_supported` named claims that no advertised scope governed.
        var builtIn = new[] { "openid", "profile", "email", "phone", "roles", "groups", "offline_access" };
        try
        {
            var custom = await scopeStore.ListAsync(ct);
            return builtIn
                .Concat(custom.Where(s => s.ShowInDiscoveryDocument).Select(s => s.Name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return builtIn;
        }
    }

    /// <summary>
    /// RFC 9396 §10 advertisement: the connector types the host's catalog exposes. Null (the
    /// member is omitted) when no catalog is registered or it is empty — resolved via the
    /// service provider because the catalog is an optional seam, not a required dependency.
    /// </summary>
    public static async Task<string[]?> ResolveAuthorityTypesAsync(IServiceProvider services, CancellationToken ct)
    {
        var catalog = services.GetService<IConnectorCatalog>();
        if (catalog is null) return null;
        try
        {
            var connectors = await catalog.GetAllAsync(ct);
            return connectors.Count > 0 ? connectors.Select(c => c.Type).ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    public static JwksDocument BuildJwksDocument(IKeyManager keyManager)
    {
        var keys = keyManager.GetSecurityKeys();
        return new JwksDocument
        {
            Keys = keys.Select(k => new JwkKey
            {
                Kty = k.Kty,
                Use = k.Use,
                Kid = k.Kid,
                Alg = k.Alg,
                Crv = k.Crv,
                X = k.X,
                Y = k.Y,
                N = k.N,
                E = k.E,
            }).ToList()
        };
    }
}
