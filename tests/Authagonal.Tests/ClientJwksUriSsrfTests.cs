using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Tests;

/// <summary>
/// F346 — the client <c>jwks_uri</c> fetch on the <c>private_key_jwt</c> path.
/// </summary>
/// <remarks>
/// The SSRF guard was applied to the registered URI and then the fetch was a raw
/// <c>HttpClient.GetStringAsync</c> on a redirect-following client, so a jwks_uri on an
/// attacker-controlled host answering <c>302 Location: https://169.254.169.254/…</c> reached an address
/// the guard had already refused. Every other server-initiated fetch in the product goes through
/// SafeOutboundHttp, which resolves hops itself so it can re-run the guard on each one; this call site
/// was the one that did not, and it is reachable from an anonymous <c>/connect/token</c> request before
/// any credential is verified.
/// <para>
/// The two tests are a pair. The refusal alone would pass even if the fetch simply stopped following
/// redirects, so the accepted case pins that hops ARE still resolved — which is only true if
/// SafeOutboundHttp is doing it, because the stub primary handler here follows nothing on its own.
/// </para>
/// </remarks>
public sealed class ClientJwksUriSsrfTests : IAsyncLifetime
{
    private const string KeyId = "jwks-uri-test-key";

    private readonly AuthagonalTestFactory _factory = new();
    private readonly RedirectingJwksHandler _jwks = new();
    private HttpClient _client = null!;
    private ECDsa _signingKey = null!;

    // Fresh per run: JwksCache is a process-wide static keyed by URI, so a fixed URI would let one
    // test's cached key set answer another's fetch.
    private readonly string _origin = $"https://jwks-{Guid.NewGuid():N}.test";

    public async Task InitializeAsync()
    {
        _factory.JwksHttpHandler = _jwks;
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = _signingKey.ExportParameters(includePrivateParameters: false);
        _jwks.Jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "EC",
                    crv = "P-256",
                    kid = KeyId,
                    use = "sig",
                    alg = "ES256",
                    x = Base64UrlEncoder.Encode(parameters.Q.X),
                    y = Base64UrlEncoder.Encode(parameters.Q.Y),
                },
            },
        });
    }

    public Task DisposeAsync()
    {
        _signingKey?.Dispose();
        _jwks.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task JwksUri_RedirectingToTheMetadataAddress_IsRefused()
    {
        var clientId = await RegisterClientAsync($"{_origin}/redirect-to-metadata");

        var response = await AuthenticateAsync(clientId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(_jwks.Requested, u => u.Contains("169.254.169.254", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JwksUri_RedirectingToAPublicHost_IsFollowed()
    {
        var clientId = await RegisterClientAsync($"{_origin}/redirect-to-keys");

        var response = await AuthenticateAsync(clientId);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the safe redirect was not followed: {(int)response.StatusCode} {body}");
        Assert.Contains($"{_origin}/keys", _jwks.Requested);
    }

    private async Task<string> RegisterClientAsync(string jwksUri)
    {
        var clientId = $"jwks-uri-client-{Guid.NewGuid():N}";
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = clientId,
            ClientName = "jwks_uri client",
            RequireClientSecret = false,
            JwksUri = jwksUri,
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid", AuthagonalTestFactory.AdminScope],
            AccessTokenLifetimeSeconds = 3600,
        });
        return clientId;
    }

    private Task<HttpResponseMessage> AuthenticateAsync(string clientId)
    {
        var securityKey = new ECDsaSecurityKey(_signingKey) { KeyId = KeyId };
        var now = DateTime.UtcNow;
        var assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = AuthagonalTestFactory.TestIssuer,
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", clientId),
                new System.Security.Claims.Claim("jti", Guid.NewGuid().ToString("N")),
            ]),
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256),
        });

        return _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
        }));
    }

    /// <summary>
    /// Answers the two redirect shapes and the key set itself, and records every URL it was asked for —
    /// the internal address must never appear there, because the guard has to refuse before the request
    /// is issued, not after the response comes back.
    /// </summary>
    private sealed class RedirectingJwksHandler : HttpMessageHandler
    {
        public volatile string Jwks = "";
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Requested = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);

            if (url.EndsWith("/redirect-to-metadata", StringComparison.Ordinal))
                return Task.FromResult(Redirect("https://169.254.169.254/latest/meta-data/keys"));

            if (url.EndsWith("/redirect-to-keys", StringComparison.Ordinal))
                return Task.FromResult(Redirect(url.Replace("/redirect-to-keys", "/keys", StringComparison.Ordinal)));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Jwks, System.Text.Encoding.UTF8, "application/json"),
            });
        }

        private static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        }
    }
}
