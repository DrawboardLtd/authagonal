using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Mock HTTP handler that simulates an OIDC Identity Provider.
/// Returns discovery document, JWKS, token exchange, and userinfo responses.
/// </summary>
public sealed class OidcMockHandler : HttpMessageHandler
{
    private static readonly Lazy<RSA> _rsa = new(() => RSA.Create(2048));
    private static RSA SigningKey => _rsa.Value;

    public string Issuer { get; set; } = "https://oidc-idp.test";
    public string Subject { get; set; } = "oidc-user-123";
    public string Email { get; set; } = "oidcuser@example.com";
    public bool EmailVerified { get; set; } = true;
    public string Name { get; set; } = "OIDC User";
    public bool FailTokenExchange { get; set; }
    public bool ReturnExpiredToken { get; set; }

    /// <summary>Set this to the nonce from the authorization request. The mock will include it in the ID token.</summary>
    public string? Nonce { get; set; }

    /// <summary>
    /// The <c>aud</c> the id_token carries. One entry emits a JSON string, more than one emits an
    /// array — which is the shape OIDC Core §3.1.3.7 steps 4-5 are about, and the shape this harness
    /// could not previously express at all.
    /// </summary>
    /// <remarks>
    /// A multi-valued aud has to be written through <c>Claims["aud"]</c> rather than
    /// <c>SecurityTokenDescriptor.Audience</c>, which is a single string. The distinction matters to
    /// the code under test for a second reason: IdentityModel surfaces a repeated claim on
    /// <c>TokenValidationResult.Claims</c> as a <c>List&lt;object&gt;</c> and a single one as a
    /// <c>string</c>, and a check that counts them wrongly is how the azp gate became dead code.
    /// </remarks>
    public string[] Audiences { get; set; } = ["test-oidc-client"];

    /// <summary>
    /// OIDC Core <c>azp</c> — the party the token was actually authorized for. Omitted when null,
    /// which is legal for a single-audience token and a MUST-reject for a multi-audience one.
    /// </summary>
    public string? Azp { get; set; }

    /// <summary>Extra claims to release on the id_token, simulating an upstream IdP that
    /// scope-gates custom claims. Tests use this to verify federation flow-through.</summary>
    public Dictionary<string, object> ExtraIdTokenClaims { get; } = new();

    public static string KeyId => "test-oidc-key-1";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";

        if (path.EndsWith("/.well-known/openid-configuration"))
            return Task.FromResult(DiscoveryResponse());

        if (path.EndsWith("/jwks"))
            return Task.FromResult(JwksResponse());

        if (path.EndsWith("/token"))
            return Task.FromResult(TokenResponse(request));

        if (path.EndsWith("/userinfo"))
            return Task.FromResult(UserinfoResponse());

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private HttpResponseMessage DiscoveryResponse()
    {
        var doc = new
        {
            issuer = Issuer,
            authorization_endpoint = $"{Issuer}/authorize",
            token_endpoint = $"{Issuer}/token",
            jwks_uri = $"{Issuer}/jwks",
            userinfo_endpoint = $"{Issuer}/userinfo",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        };

        return JsonResponse(doc);
    }

    private HttpResponseMessage JwksResponse()
    {
        var pubParams = SigningKey.ExportParameters(false);
        var jwk = new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = KeyId,
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(pubParams.Modulus!),
                    e = Base64UrlEncoder.Encode(pubParams.Exponent!)
                }
            }
        };
        return JsonResponse(jwk);
    }

    private HttpResponseMessage TokenResponse(HttpRequestMessage request)
    {
        if (FailTokenExchange)
            return JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);

        var now = DateTime.UtcNow;
        var handler = new JsonWebTokenHandler();
        var key = new RsaSecurityKey(SigningKey) { KeyId = KeyId };

        var claims = new Dictionary<string, object>
        {
            ["sub"] = Subject,
            ["email"] = Email,
            ["email_verified"] = EmailVerified,
            ["name"] = Name,
        };

        // Include nonce if set (must match the one from the authorization request)
        if (Nonce is not null)
            claims["nonce"] = Nonce;

        if (Azp is not null)
            claims["azp"] = Azp;

        foreach (var (k, v) in ExtraIdTokenClaims)
            claims[k] = v;

        // Descriptor.Audience is a single string, so a multi-valued aud has to go through the claim
        // bag. Exactly one of the two is set — supplying both makes the emitted aud ambiguous.
        if (Audiences.Length != 1)
            claims["aud"] = Audiences;

        var idTokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audiences.Length == 1 ? Audiences[0] : null,
            IssuedAt = now,
            Expires = ReturnExpiredToken ? now.AddMinutes(-5) : now.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            Claims = claims
        };

        var idToken = handler.CreateToken(idTokenDescriptor);
        var accessToken = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "test-oidc-client",
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = Subject }
        });

        return JsonResponse(new
        {
            access_token = accessToken,
            id_token = idToken,
            token_type = "Bearer",
            expires_in = 3600,
        });
    }

    private HttpResponseMessage UserinfoResponse()
    {
        return JsonResponse(new
        {
            sub = Subject,
            email = Email,
            email_verified = EmailVerified,
            name = Name,
            given_name = "OIDC",
            family_name = "User",
        });
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
