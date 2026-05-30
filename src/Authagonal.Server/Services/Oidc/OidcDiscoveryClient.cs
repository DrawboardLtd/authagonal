using System.Text.Json;
using Authagonal.Server.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services.Oidc;

public sealed record OidcDiscoveryDocument(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string JwksUri,
    string Issuer,
    string? UserinfoEndpoint,
    List<JsonWebKey> SigningKeys);

public sealed class OidcDiscoveryClient(IHttpClientFactory httpClientFactory, IMemoryCache memoryCache, IOptions<CacheOptions> cacheOptions)
{

    public async Task<OidcDiscoveryDocument> GetDiscoveryAsync(string metadataUrl, CancellationToken ct = default)
    {
        var cacheKey = $"oidc-discovery:{metadataUrl}";
        if (memoryCache.TryGetValue<OidcDiscoveryDocument>(cacheKey, out var cached) && cached is not null)
            return cached;

        if (!OutboundUrlValidator.IsSafe(metadataUrl))
            throw new InvalidOperationException("OIDC metadata URL is not an allowed external endpoint.");

        var client = httpClientFactory.CreateClient("OidcDiscovery");

        // Fetch the discovery document
        var discoveryJson = await client.GetStringAsync(metadataUrl, ct);
        using var discoveryDoc = JsonDocument.Parse(discoveryJson);
        var root = discoveryDoc.RootElement;

        var authorizationEndpoint = root.GetProperty("authorization_endpoint").GetString()
            ?? throw new InvalidOperationException("Discovery document missing authorization_endpoint");

        var tokenEndpoint = root.GetProperty("token_endpoint").GetString()
            ?? throw new InvalidOperationException("Discovery document missing token_endpoint");

        var jwksUri = root.GetProperty("jwks_uri").GetString()
            ?? throw new InvalidOperationException("Discovery document missing jwks_uri");

        var issuer = root.GetProperty("issuer").GetString()
            ?? throw new InvalidOperationException("Discovery document missing issuer");

        string? userinfoEndpoint = null;
        if (root.TryGetProperty("userinfo_endpoint", out var userinfoElement))
            userinfoEndpoint = userinfoElement.GetString();

        // The discovery document is attacker-influenced (it comes from whatever the metadata URL
        // resolves to), so the endpoints WE later fetch must also pass the SSRF guard.
        if (!OutboundUrlValidator.IsSafe(jwksUri) || !OutboundUrlValidator.IsSafe(tokenEndpoint)
            || (userinfoEndpoint is not null && !OutboundUrlValidator.IsSafe(userinfoEndpoint)))
            throw new InvalidOperationException("OIDC discovery document referenced a disallowed endpoint URL.");

        // Fetch JWKS
        var jwksJson = await client.GetStringAsync(jwksUri, ct);
        var jwks = JsonWebKeySet.Create(jwksJson);

        var document = new OidcDiscoveryDocument(
            authorizationEndpoint,
            tokenEndpoint,
            jwksUri,
            issuer,
            userinfoEndpoint,
            [.. jwks.Keys]);

        memoryCache.Set(cacheKey, document, TimeSpan.FromMinutes(cacheOptions.Value.OidcDiscoveryCacheMinutes));
        return document;
    }
}
