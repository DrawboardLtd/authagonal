using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Tests;

/// <summary>
/// F255 / F311 — the endpoints RFC 7009 §2.1 and RFC 7662 §2.1 require a client to authenticate to
/// must accept the same authentication methods the token endpoint does. Both carried a private copy
/// of the client-auth logic that understood only client_secret_basic/_post, so a client whose only
/// registered credential is a key could not reach either one.
/// </summary>
public sealed class ClientAssertionEndpointTests : IAsyncLifetime
{
    private const string KeyClientId = "key-only-client";
    private const string KeyId = "assertion-test-key";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private ECDsa _signingKey = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Only the public half is registered — the point of private_key_jwt is that the server never
        // holds anything replayable.
        var parameters = _signingKey.ExportParameters(includePrivateParameters: false);
        var jwks = JsonSerializer.Serialize(new
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

        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = KeyClientId,
            ClientName = "Key-only client",
            // No secret at all. This is the registration shape the two endpoints could not serve.
            RequireClientSecret = false,
            JwksJson = jwks,
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid", AuthagonalTestFactory.AdminScope],
            AccessTokenLifetimeSeconds = 3600,
        });
    }

    public Task DisposeAsync()
    {
        _signingKey?.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task Introspect_WithClientAssertion_Authenticates()
    {
        var token = await MintTokenAsync();

        var response = await PostWithAssertionAsync("/connect/introspect", new() { ["token"] = token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("active").GetBoolean(),
            "the client authenticated by assertion but introspection did not recognise its own token");
    }

    [Fact]
    public async Task Revocation_WithClientAssertion_ActuallyRevokes()
    {
        var token = await MintTokenAsync();

        var revoke = await PostWithAssertionAsync("/connect/revocation", new() { ["token"] = token });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // RFC 7009 answers 200 whatever happens, so the status proves nothing on its own — the token
        // has to actually be dead afterwards. Introspection is the observable.
        var introspect = await PostWithAssertionAsync("/connect/introspect", new() { ["token"] = token });
        var json = await introspect.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("active").GetBoolean(),
            "revocation returned 200 without revoking anything");
    }

    [Fact]
    public async Task Introspect_WithForgedAssertion_IsRefused()
    {
        var token = await MintTokenAsync();

        // A different key, presented for the same client_id. Accepting this would make the JWKS
        // decorative.
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = BuildAssertion(otherKey);

        var response = await _client.PostAsync("/connect/introspect", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = token,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = forged,
            }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnAssertionIsSingleUse()
    {
        // RFC 7523 makes the jti single-use, so a captured assertion is worth nothing after its first
        // presentation.
        //
        // Sequential only, deliberately. The defect was that this was enforced as IsRevokedAsync then
        // AddAsync over backends whose AddAsync is an unconditional upsert, which two CONCURRENT
        // presentations both slip through — but that window is too narrow to provoke reliably through
        // TestServer, and a test that reproduces a race only sometimes is worse than no test. The
        // atomicity is asserted where it lives, on the store:
        // SqlProviderTestsBase.RevokedTokenStore_ClaimHasExactlyOneWinnerUnderConcurrency, which runs
        // against SQLite and a real PostgreSQL.
        var assertion = BuildAssertion(_signingKey);

        Task<HttpResponseMessage> Present() => _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = AuthagonalTestFactory.AdminScope,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
            }));

        Assert.True((await Present()).IsSuccessStatusCode);

        var replay = await Present();
        Assert.False(replay.IsSuccessStatusCode);
        Assert.Contains("already been used", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJtiThatIsHostileAsAStorageKey_IsStillAccepted()
    {
        // The jti is a client-controlled claim and was used verbatim as an Azure Table RowKey, where
        // '/', '\\', '#', '?', a control character or 1024+ characters is a 400 that neither store
        // path handled. Hashing the composite key makes it fixed-width and charset-safe.
        //
        // This passes with or without the hashing, because the in-memory store has no key charset or
        // length limit — only Azure Table does. It is kept as the guard that hashing did not break
        // an ordinary jti; the exposure it describes is provider-specific and not reproducible here.
        var assertion = BuildAssertion(_signingKey, jti: "a/b\\c#d?e" + new string('x', 2000));

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = AuthagonalTestFactory.AdminScope,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
            }));

        Assert.True(response.IsSuccessStatusCode,
            $"a well-formed assertion was refused because of its jti: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Introspect_FromPublicClient_IsStillRefused()
    {
        // The anti-scanning rule (RFC 7662 §2.1) has to survive the rework: a public client naming
        // itself has proved nothing, and its client_id ships in a browser bundle.
        var token = await MintTokenAsync();

        var response = await _client.PostAsync("/connect/introspect", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = token,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<string> MintTokenAsync()
    {
        var response = await PostWithAssertionAsync("/connect/token", new()
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope,
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"token request failed: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> PostWithAssertionAsync(string path, Dictionary<string, string> form)
    {
        form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        form["client_assertion"] = BuildAssertion(_signingKey);
        return _client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    /// <summary>
    /// A fresh jti each time — assertions are single-use, so a shared one would make the second call
    /// in any test fail for the wrong reason.
    /// </summary>
    private static string BuildAssertion(ECDsa key, string? jti = null)
    {
        var securityKey = new ECDsaSecurityKey(key) { KeyId = KeyId };
        var now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = KeyClientId,
            Audience = AuthagonalTestFactory.TestIssuer,
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", KeyClientId),
                new System.Security.Claims.Claim("jti", jti ?? Guid.NewGuid().ToString("N")),
            ]),
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256),
        });
    }
}
