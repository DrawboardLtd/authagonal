using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Authagonal.Bff;

/// <summary>Discovers and caches each tenant's OIDC metadata (endpoints + signing keys), keyed by authority,
/// refreshing on the usual schedule and on signing-key rotation. One <see cref="ConfigurationManager{T}"/> per
/// authority — so a single multi-tenant BFF discovers each tenant's auth host independently. Singleton.</summary>
/// <remarks>
/// The discovery document is the trust anchor for the whole connection: <c>issuer</c> read out of it becomes
/// <c>ValidIssuer</c> in <c>CallbackAsync</c>, and <c>jwks_uri</c> read out of it supplies the keys every
/// id_token is checked against. Both halves of that comparison came from the same document, so anyone who can
/// answer the metadata URL could mint an id_token for any <c>sub</c> and receive a BFF session cookie for that
/// user. The server's own federation path (<c>Authagonal.Server/Services/Oidc/OidcDiscoveryClient</c>) has
/// carried controls against this; this client had none of them, and neither did its TypeScript twin.
/// </remarks>
public sealed class BffOidcConfig
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current OIDC configuration for the given authority (cached; refreshed automatically).</summary>
    public async Task<OpenIdConnectConfiguration> GetAsync(string authority, CancellationToken ct = default)
    {
        var normalized = authority.TrimEnd('/');

        var config = await _managers.GetOrAdd(normalized, static a =>
        {
            var metadataAddress = $"{a}/.well-known/openid-configuration";
            var requireHttps = a.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = requireHttps });
        }).GetConfigurationAsync(ct);

        Validate(normalized, config);
        return config;
    }

    /// <summary>
    /// The two trust-anchor checks this client can make without breaking a supported topology.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Issuer binding (OIDC Discovery §4.3).</b> The <c>issuer</c> a document declares MUST match the URL it
    /// was served from. Unenforced, <c>issuer</c> is simply whatever the document's author chose, while
    /// <c>CallbackAsync</c> validates the id_token with <c>ValidIssuer = config.Issuer</c> — checking the forged
    /// document against itself. Binding them makes the operator's CONFIGURED authority the thing that is
    /// actually trusted, and it is the check that closes the authentication bypass regardless of scheme.
    /// </para>
    /// <para>
    /// <b>No endpoint weaker than the authority.</b> <c>HttpDocumentRetriever.RequireHttps</c> constrains the
    /// metadata address and nothing else, so a document could name <c>http://…</c> for
    /// <c>token_endpoint</c> (which carries the client secret and the authorization code),
    /// <c>jwks_uri</c> (the signing keys), or <c>end_session_endpoint</c> (which
    /// <c>GET /bff/logout</c> redirects the browser to with <c>id_token_hint</c> attached) even when the
    /// authority itself was https. An http authority is left alone deliberately: reaching an identity server on
    /// a private address is a topology this library explicitly supports, and such a deployment has already
    /// accepted plaintext on that path. What it has not accepted is being downgraded to plaintext by a value
    /// the document chose.
    /// </para>
    /// </remarks>
    internal static void Validate(string authority, OpenIdConnectConfiguration config)
    {
        var declared = config.Issuer?.TrimEnd('/');
        if (!string.Equals(declared, authority, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"OIDC discovery issuer mismatch: the document for authority '{authority}' declares issuer "
                + $"'{config.Issuer}'. Per OIDC Discovery §4.3 they must match. Refusing rather than trusting "
                + "an issuer the configured authority does not vouch for — the id_token is validated against "
                + "this value, so accepting it would let whoever served the document authenticate as any user.");

        if (!authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var (name, endpoint) in new (string, string?)[]
                 {
                     ("jwks_uri", config.JwksUri),
                     ("token_endpoint", config.TokenEndpoint),
                     ("authorization_endpoint", config.AuthorizationEndpoint),
                     ("userinfo_endpoint", config.UserInfoEndpoint),
                     ("end_session_endpoint", config.EndSessionEndpoint),
                 })
        {
            if (string.IsNullOrEmpty(endpoint)) continue;
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
                continue;

            throw new InvalidOperationException(
                $"OIDC discovery document for https authority '{authority}' declared a non-https {name} "
                + $"('{endpoint}'). That would move the signing keys, the client secret, the authorization "
                + "code or the id_token over cleartext on a connection the operator configured as https.");
        }
    }
}
