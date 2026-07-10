using Authagonal.Core.Services;
using Authagonal.Core.Stores;

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
        var builtIn = new[] { "openid", "profile", "email", "offline_access" };
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
