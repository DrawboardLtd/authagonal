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
