using Authagonal.Bff;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Authagonal.Tests;

/// <summary>
/// The BFF's discovery document is the trust anchor for the whole connection, and nothing checked it.
/// </summary>
/// <remarks>
/// <c>issuer</c> read out of the document becomes <c>ValidIssuer</c> in <c>CallbackAsync</c>, and
/// <c>jwks_uri</c> read out of the same document supplies the keys the id_token is verified against — so both
/// halves of that comparison came from one place. Anyone able to answer the metadata URL could mint an id_token
/// for any <c>sub</c>, walk the flow once, and be issued a BFF session cookie for that user: a full
/// authentication bypass, and no later check can recover from it.
/// <para>
/// <c>HttpDocumentRetriever.RequireHttps</c> was the only control, it applied to the metadata address alone,
/// and <c>requireHttps = a.StartsWith("https://")</c> turned it off entirely for the private-network authority
/// the package explicitly supports. The server's own federation path
/// (<c>Authagonal.Server/Services/Oidc/OidcDiscoveryClient</c>) has carried these checks for a while; this
/// client and its TypeScript twin had none of them.
/// </para>
/// <para>
/// The TypeScript twin asserts the same four cases in <c>bff-lib/test/bff-security.test.js</c>, because
/// parity between the two implementations is the standing rule for this package and its absence is what
/// produced this defect.
/// </para>
/// </remarks>
public class BffDiscoveryTrustTests
{
    private static OpenIdConnectConfiguration Doc(
        string issuer,
        string? jwks = null,
        string? token = null,
        string? authorize = null,
        string? endSession = null) => new()
    {
        Issuer = issuer,
        JwksUri = jwks ?? "https://auth.example/.well-known/jwks",
        TokenEndpoint = token ?? "https://auth.example/connect/token",
        AuthorizationEndpoint = authorize ?? "https://auth.example/connect/authorize",
        EndSessionEndpoint = endSession,
    };

    [Fact]
    public void ADocumentDeclaringSomeoneElseAsIssuerIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BffOidcConfig.Validate("https://auth.example", Doc("https://evil.example")));

        Assert.Contains("issuer mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchingIssuerIsAccepted_TrailingSlashAndCaseNotwithstanding()
    {
        BffOidcConfig.Validate("https://auth.example", Doc("https://auth.example/"));
        BffOidcConfig.Validate("https://auth.example", Doc("https://AUTH.example"));
    }

    /// <summary>
    /// An https authority must not be downgraded by a value the document chose.
    /// </summary>
    /// <remarks>
    /// Each of these is a full compromise on its own: <c>jwks_uri</c> is the key set every id_token is checked
    /// against, <c>token_endpoint</c> carries the client secret and the authorization code, and
    /// <c>end_session_endpoint</c> is where <c>GET /bff/logout</c> — mapped for GET, requiring no anti-forgery
    /// header, so triggerable from any third-party page with an <c>img</c> — sends the browser with
    /// <c>id_token_hint</c> attached.
    /// </remarks>
    [Fact]
    public void AnHttpsAuthorityRefusesAPlaintextEndpointTheDocumentNames()
    {
        Assert.Throws<InvalidOperationException>(() => BffOidcConfig.Validate(
            "https://auth.example", Doc("https://auth.example", jwks: "http://evil.example/jwks")));

        Assert.Throws<InvalidOperationException>(() => BffOidcConfig.Validate(
            "https://auth.example", Doc("https://auth.example", token: "http://evil.example/t")));

        Assert.Throws<InvalidOperationException>(() => BffOidcConfig.Validate(
            "https://auth.example", Doc("https://auth.example", endSession: "http://evil.example/e")));
    }

    /// <summary>
    /// The private-network topology the package documents as supported keeps working — but still binds issuer.
    /// </summary>
    [Fact]
    public void APrivateNetworkHttpAuthorityStaysSupported_ButTheIssuerBindingStillApplies()
    {
        BffOidcConfig.Validate("http://auth.internal:8080", new OpenIdConnectConfiguration
        {
            Issuer = "http://auth.internal:8080",
            JwksUri = "http://auth.internal:8080/.well-known/jwks",
            TokenEndpoint = "http://auth.internal:8080/connect/token",
            AuthorizationEndpoint = "http://auth.internal:8080/connect/authorize",
        });

        // The attack the finding describes: a document served on that private path naming itself as a
        // different issuer and pointing jwks_uri at the attacker.
        Assert.Throws<InvalidOperationException>(() => BffOidcConfig.Validate(
            "http://auth.internal:8080", new OpenIdConnectConfiguration
            {
                Issuer = "https://evil.example",
                JwksUri = "https://evil.example/jwks",
                TokenEndpoint = "https://evil.example/t",
                AuthorizationEndpoint = "http://auth.internal:8080/connect/authorize",
            }));
    }
}
