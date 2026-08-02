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

/// <param name="allowlist">
/// The operator's internal-destination allowlist (<c>Auth:AllowedInternalTargets</c>). Applied to the
/// metadata URL, to every redirect, and to the endpoints the document names — an on-premises IdP publishes
/// its jwks_uri and token_endpoint on the same private network the discovery URL is on, so permitting only
/// the first of those would leave every connection failing one hop later. Optional, and absent means
/// strict: every internal target refused, which is what this did before the allowlist existed. It must be
/// the same list the "OidcDiscovery" client's connect callback was given.
/// </param>
public sealed class OidcDiscoveryClient(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    IOptions<CacheOptions> cacheOptions,
    Authagonal.Core.Services.OutboundAllowlist? allowlist = null)
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
        var discoveryJson = await SafeOutboundHttp.GetStringAsync(
            client, metadataUrl, ct: ct, allowlist: allowlist);
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
        // resolves to), so the endpoints WE later fetch must also pass the SSRF guard. The operator's
        // allowlist applies to these too: the document that named them was fetched from a URL the operator
        // configured and bound to its own issuer above, and an on-premises IdP publishes all of its
        // endpoints on the private network its discovery URL is on. Permitting the discovery URL and then
        // refusing the jwks_uri beside it would be a guard that only ever half-worked.
        if (!OutboundUrlValidator.IsSafe(jwksUri, allowlist) || !OutboundUrlValidator.IsSafe(tokenEndpoint, allowlist)
            || (userinfoEndpoint is not null && !OutboundUrlValidator.IsSafe(userinfoEndpoint, allowlist)))
            throw new InvalidOperationException("OIDC discovery document referenced a disallowed endpoint URL.");

        // And they must be https, which the SSRF guard deliberately does not decide — it permits http by
        // design, because scheme policy belongs to the caller. That left this path requiring https on the
        // metadata URL and then accepting whatever scheme the document named for everything else, which is
        // the same defect one hop further down: jwks_uri supplies the keys every upstream id_token is
        // validated against, so fetched over cleartext an on-path party substitutes the key set and the
        // callback signs their assertion in as any user. token_endpoint carries this connection's client
        // secret and the authorization code; userinfo_endpoint carries the access token; and
        // authorization_endpoint is where the user's own browser is sent to authenticate. Every one of them
        // is a full compromise of the connection in cleartext, and refusing three while accepting the
        // fourth is how the sibling gets missed.
        foreach (var (name, endpoint) in new (string, string?)[]
                 {
                     ("jwks_uri", jwksUri),
                     ("token_endpoint", tokenEndpoint),
                     ("userinfo_endpoint", userinfoEndpoint),
                     ("authorization_endpoint", authorizationEndpoint),
                 })
        {
            if (endpoint is null) continue;
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                && endpointUri.Scheme == Uri.UriSchemeHttps)
                continue;

            throw new InvalidOperationException(
                $"OIDC discovery document declared a non-https {name} ('{endpoint}'). The document itself is "
                + "required to be https because it is this connection's trust anchor; an endpoint it names "
                + "that is reached over cleartext hands the same material — the signing keys, the client "
                + "secret, the authorization code, the access token — to anyone on the network path.");
        }

        // Fetch JWKS — same per-hop validation.
        var jwksJson = await SafeOutboundHttp.GetStringAsync(client, jwksUri, ct: ct, allowlist: allowlist);
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
