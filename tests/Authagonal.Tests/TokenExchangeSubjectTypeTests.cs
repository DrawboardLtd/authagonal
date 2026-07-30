using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Tests;

/// <summary>
/// RFC 8693 token exchange validated the <c>subject_token</c>'s issuer, audience, lifetime and signature
/// but never its KIND. All four JWT mint sites in this server share one issuer and one signing key, and
/// every one emitted the default <c>typ: JWT</c> — so an id_token or a back-channel logout token satisfied
/// every check and was exchanged for a live access token carrying the victim's <c>sub</c> and roles.
///
/// The sting: neither an id_token nor a logout token carries a <c>jti</c>, so the 0.20.0 revocation check
/// silently degraded to a no-op on this path, and <c>RevocationEndpoint</c> (which requires both
/// <c>client_id</c> and <c>jti</c>) could not revoke them even in principle — there was no operator remedy
/// short of rotating signing keys.
///
/// Derived from the review's working proof-of-concept, inverted to assert the exchange is refused.
/// </summary>
public sealed class TokenExchangeSubjectTypeTests : IAsyncLifetime
{
    private const string AppScope = "projects-api.read";
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        var client = (await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!;
        client.AllowedGrantTypes = [.. client.AllowedGrantTypes, GrantTypes.TokenExchange];
        client.AllowedScopes = [.. client.AllowedScopes, AppScope];
        await _factory.ClientStore.UpsertAsync(client);
        await _factory.ScopeStore.CreateAsync(new Scope { Name = AppScope, UserClaims = ["org_role"] });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>An id_token is not an access token, however it is labelled on the wire.</summary>
    [Theory]
    [InlineData("urn:ietf:params:oauth:token-type:access_token")]
    [InlineData("urn:ietf:params:oauth:token-type:jwt")]
    [InlineData("urn:ietf:params:oauth:token-type:id_token")]
    public async Task IdToken_cannot_be_exchanged(string subjectTokenType)
    {
        var (_, idToken) = await GetTokensAsync();
        Assert.NotNull(idToken);

        var response = await ExchangeAsync(idToken!, subjectTokenType);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A back-channel logout token is signed by the same key and passed every check. Minted here with the
    /// server's own key manager, in the exact shape BackChannelLogoutEndpoint emits.
    /// </summary>
    [Fact]
    public async Task LogoutToken_cannot_be_exchanged()
    {
        var (_, idToken) = await GetTokensAsync();
        var keyManager = (IKeyManager)_factory.Services.GetService(typeof(IKeyManager))!;
        var tenant = (ITenantContext)_factory.Services.GetService(typeof(ITenantContext))!;
        var sub = new JsonWebTokenHandler().ReadJsonWebToken(idToken!).GetPayloadValue<string>("sub");

        var logoutToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = tenant.Issuer,
            Audience = AuthagonalTestFactory.TestClientId,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(2),
            TokenType = TokenTypes.LogoutJwt,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = sub,
                ["events"] = new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>(),
                },
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = keyManager.GetSigningCredentials(),
        });

        var response = await ExchangeAsync(logoutToken, "urn:ietf:params:oauth:token-type:jwt");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A logout token forged with the OLD shape — no explicit typ, so it inherits `typ: JWT` exactly as
    /// before the fix — must also be refused. This is what the belt-and-braces claim checks are for:
    /// pinning ValidTypes alone would not stop it if a handler treated a missing typ as acceptable.
    /// </summary>
    [Fact]
    public async Task Untyped_logout_shaped_token_cannot_be_exchanged()
    {
        var (_, idToken) = await GetTokensAsync();
        var keyManager = (IKeyManager)_factory.Services.GetService(typeof(IKeyManager))!;
        var tenant = (ITenantContext)_factory.Services.GetService(typeof(ITenantContext))!;
        var sub = new JsonWebTokenHandler().ReadJsonWebToken(idToken!).GetPayloadValue<string>("sub");

        var legacyShaped = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = tenant.Issuer,
            Audience = AuthagonalTestFactory.TestClientId,
            IssuedAt = DateTime.UtcNow,
            // No TokenType: the pre-fix header.
            Claims = new Dictionary<string, object>
            {
                ["sub"] = sub,
                ["events"] = new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>(),
                },
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = keyManager.GetSigningCredentials(),
        });

        var response = await ExchangeAsync(legacyShaped, "urn:ietf:params:oauth:token-type:jwt");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A genuine access token must still exchange — guards against over-tightening.</summary>
    [Fact]
    public async Task AccessToken_still_exchanges()
    {
        var (accessToken, _) = await GetTokensAsync();

        var response = await ExchangeAsync(accessToken, "urn:ietf:params:oauth:token-type:access_token");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var minted = JsonDocument.Parse(raw).RootElement.GetProperty("access_token").GetString()!;
        var token = new JsonWebTokenHandler().ReadJsonWebToken(minted);

        // And the minted token carries the RFC 9068 type, so it is itself exchangeable and inspectable.
        Assert.Equal(TokenTypes.AccessTokenJwt, token.GetHeaderValue<string>("typ"));
    }

    /// <summary>Access tokens carry typ at+jwt, so their kind is verifiable rather than implied.</summary>
    [Fact]
    public async Task AccessToken_is_stamped_at_jwt()
    {
        var (accessToken, _) = await GetTokensAsync();
        var token = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);
        Assert.Equal(TokenTypes.AccessTokenJwt, token.GetHeaderValue<string>("typ"));
    }

    /// <summary>
    /// Cross-JWT confusion at the read paths (#111): an id_token is issued TO the client, not as a
    /// credential for calling the OP, yet it satisfied userinfo's signature/issuer/lifetime checks.
    /// </summary>
    [Fact]
    public async Task IdToken_is_refused_at_userinfo()
    {
        var (accessToken, idToken) = await GetTokensAsync();

        using var withId = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        withId.Headers.Authorization = new("Bearer", idToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(withId)).StatusCode);

        // The real access token still works.
        using var withAccess = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        withAccess.Headers.Authorization = new("Bearer", accessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(withAccess)).StatusCode);
    }

    /// <summary>Introspection must not report an id_token as an active token.</summary>
    [Fact]
    public async Task IdToken_is_inactive_at_introspection()
    {
        var (accessToken, idToken) = await GetTokensAsync();

        var idRes = await _client.PostAsync("/connect/introspect", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["token"] = idToken!, ["client_id"] = AuthagonalTestFactory.TestClientId }));
        if (idRes.StatusCode == HttpStatusCode.OK)
        {
            var body = JsonDocument.Parse(await idRes.Content.ReadAsStringAsync()).RootElement;
            Assert.False(body.GetProperty("active").GetBoolean());
        }

        var acRes = await _client.PostAsync("/connect/introspect", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["token"] = accessToken, ["client_id"] = AuthagonalTestFactory.TestClientId }));
        if (acRes.StatusCode == HttpStatusCode.OK)
        {
            var body = JsonDocument.Parse(await acRes.Content.ReadAsStringAsync()).RootElement;
            Assert.True(body.GetProperty("active").GetBoolean(), "a genuine access token must introspect as active");
        }
    }

    /// <summary>
    /// Authority forgery (#12). For a client with no agent profile the exchange computed
    /// <c>subjectAuthority.Intersect(requestedAuthority)</c>, and <c>ReadAuthorityClaim</c> yields
    /// <c>Unrestricted</c> when the subject token has no <c>authorization_details</c> claim — the universal
    /// case, since no token issued via authorization-code, refresh, device or profile-less
    /// client-credentials carries one. <c>Unrestricted.Intersect(x)</c> returns <c>x</c> VERBATIM, so the
    /// client's request became the claim the AS signed: fine-grained authority no admin ceiling, consent
    /// record or user interaction ever produced.
    /// </summary>
    [Fact]
    public async Task Client_without_an_agent_profile_cannot_originate_authority()
    {
        var (accessToken, _) = await GetTokensAsync();

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.TokenExchange,
                ["subject_token"] = accessToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["client_id"] = AuthagonalTestFactory.TestClientId,
                ["authorization_details"] =
                    """[{"type":"payment_initiation","actions":["initiate"],"instructedAmount":{"amount":"1000000"}}]""",
            }));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("authorization_details", raw);
    }

    /// <summary>
    /// The same exchange WITHOUT authorization_details must still succeed — the guard refuses originating
    /// authority, not plain delegation.
    /// </summary>
    [Fact]
    public async Task Exchange_without_authorization_details_still_succeeds()
    {
        var (accessToken, _) = await GetTokensAsync();
        var response = await ExchangeAsync(accessToken, "urn:ietf:params:oauth:token-type:access_token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And the minted token asserts no authority.
        var minted = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("access_token").GetString()!;
        var token = new JsonWebTokenHandler().ReadJsonWebToken(minted);
        Assert.False(token.TryGetPayloadValue<object>("authorization_details", out _));
    }

    private Task<HttpResponseMessage> ExchangeAsync(string subjectToken, string subjectTokenType)
        => _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.TokenExchange,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = subjectTokenType,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));

    private async Task<(string AccessToken, string? IdToken)> GetTokensAsync()
    {
        var user = await _factory.SeedTestUserAsync();
        user.CustomAttributes["org_role"] = "admin";
        user.Roles = ["tenant-admin", "billing"];
        await _factory.UserStore.UpdateAsync(user);

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope={Uri.EscapeDataString($"openid profile email offline_access {AppScope}")}" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = verifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("access_token").GetString()!,
                body.TryGetProperty("id_token", out var it) ? it.GetString() : null);
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
