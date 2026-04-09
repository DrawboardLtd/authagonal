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
    public string Name { get; set; } = "OIDC User";
    public bool FailTokenExchange { get; set; }
    public bool ReturnExpiredToken { get; set; }

    /// <summary>Set this to the nonce from the authorization request. The mock will include it in the ID token.</summary>
    public string? Nonce { get; set; }

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
            ["name"] = Name,
        };

        // Include nonce if set (must match the one from the authorization request)
        if (Nonce is not null)
            claims["nonce"] = Nonce;

        var idTokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "test-oidc-client",
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
