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

        // OIDC Discovery §4: the metadata document is the trust anchor for the entire connection —
        // `issuer` read out of it becomes ValidIssuer, and `jwks_uri` read out of it supplies the keys
        // every upstream id_token is checked against. Fetched over plaintext, an on-path or
        // DNS-positioned attacker substitutes both together and every token then validates against
        // their keys, so the callback signs their assertion in as any user. That is a full
        // authentication bypass for the connection, and no later check can recover from it because
        // both halves of the comparison came from the same forged document.
        //
        // The SAML sibling has required https at its metadata URL since the outbound-URL work
        // (Services/Saml/SamlMetadataParser.cs). This path did not, which is the whole of the defect.
        if (!Uri.TryCreate(metadataUrl, UriKind.Absolute, out var metadataUri)
            || !string.Equals(metadataUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The OIDC discovery URL must be absolute and use https: it supplies the issuer and the "
                + "signing keys this connection trusts, so fetching it over plaintext lets anyone on the "
                + "network path substitute both and authenticate as any user.");

        var client = httpClientFactory.CreateClient("OidcDiscovery");

        // Fetch the discovery document. SafeOutboundHttp validates this URL and every redirect target —
        // the guard here previously ran once and the client then followed redirects on its own, which is the
        // hop it never inspected. This file already understood the principle (see the comment below about
        // re-validating document-derived endpoints); it just could not enforce it across a redirect.
        var discoveryJson = await SafeOutboundHttp.GetStringAsync(client, metadataUrl, ct: ct);
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

        // OIDC Discovery §4.3: the issuer in the document MUST match the URL the document was fetched
        // from. Without the binding, `issuer` is simply whatever the document says it is — a value the
        // document's author chooses — while the operator believes they configured a specific upstream.
        // Anyone who can serve this URL can therefore claim to BE any issuer, and ValidIssuer downstream
        // (OidcEndpoints.cs, `ValidIssuer = discovery.Issuer`) checks the forged document against
        // itself. Binding them makes the operator's configured URL the thing that is actually trusted.
        var expectedMetadataUrl = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        if (!string.Equals(metadataUrl, expectedMetadataUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"OIDC discovery issuer mismatch: the document at '{metadataUrl}' declares issuer "
                + $"'{issuer}', which per OIDC Discovery §4.3 should be published at "
                + $"'{expectedMetadataUrl}'. Refusing rather than trusting an issuer the configured URL "
                + "does not vouch for.");

        string? userinfoEndpoint = null;
        if (root.TryGetProperty("userinfo_endpoint", out var userinfoElement))
            userinfoEndpoint = userinfoElement.GetString();

        // The discovery document is attacker-influenced (it comes from whatever the metadata URL
        // resolves to), so the endpoints WE later fetch must also pass the SSRF guard.
        if (!OutboundUrlValidator.IsSafe(jwksUri) || !OutboundUrlValidator.IsSafe(tokenEndpoint)
            || (userinfoEndpoint is not null && !OutboundUrlValidator.IsSafe(userinfoEndpoint)))
            throw new InvalidOperationException("OIDC discovery document referenced a disallowed endpoint URL.");

        // Fetch JWKS — same per-hop validation.
        var jwksJson = await SafeOutboundHttp.GetStringAsync(client, jwksUri, ct: ct);
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
