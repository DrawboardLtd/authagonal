using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The OAuth/OIDC residuals the 2026-08-01 comparative re-run found: fixes that landed on one host
/// or one call site and not on its twin. Every test here is written against the side that was
/// MISSED — the Protocol host's userinfo, the device endpoint's credential source, the error paths
/// that hand-build a response instead of going through the shared helper.
/// </summary>
public sealed class ProtocolHostOAuthResidualTests : IAsyncLifetime
{
    private readonly ProtocolTestHost _host = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _host.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    // ── #216 / #341 — userinfo method set and challenge ─────────────

    [Fact]
    public async Task Userinfo_Post_IsAccepted()
    {
        var accessToken = await MintAccessTokenAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProtocolTestHost.TestSubjectId, claims.GetProperty("sub").GetString());
    }

    /// <summary>
    /// RFC 6750 §2.2 — the token may be presented as a form field. OIDC Core §5.3.1 routes userinfo
    /// through "Section 2 of RFC 6750", not through §2.1 alone.
    /// </summary>
    [Fact]
    public async Task Userinfo_PostWithFormAccessToken_IsAccepted()
    {
        var accessToken = await MintAccessTokenAsync();

        var response = await _client.PostAsync("/connect/userinfo", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["access_token"] = accessToken }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProtocolTestHost.TestSubjectId, claims.GetProperty("sub").GetString());
    }

    /// <summary>RFC 6750 §2: a request may not carry the token by two methods at once.</summary>
    [Fact]
    public async Task Userinfo_TokenInHeaderAndForm_IsRefused()
    {
        var accessToken = await MintAccessTokenAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/userinfo")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = accessToken }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("")]                 // no credential at all
    [InlineData("not-a-jwt")]        // unparseable
    public async Task Userinfo_Rejections_CarryBearerChallenge(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        if (token.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("realm=\"userinfo\"", challenge.Parameter);
        Assert.Contains("error=\"invalid_token\"", challenge.Parameter);
    }

    // ── #270 / #308 — userinfo consults the revoked-token store ──────

    [Fact]
    public async Task Userinfo_RevokedAccessToken_IsRefused()
    {
        var accessToken = await MintAccessTokenAsync();

        // Live before revocation — otherwise the assertion below proves nothing.
        var before = await GetUserinfoAsync(accessToken);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var jti = DecodeJwtPayload(accessToken).GetProperty("jti").GetString()!;
        await _host.RevokedTokenStore.AddAsync(jti, DateTimeOffset.UtcNow.AddHours(1));

        var after = await GetUserinfoAsync(accessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Contains(after.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    // ── #217 — caching headers ──────────────────────────────────────

    [Fact]
    public async Task Userinfo_Success_CarriesNoStore()
    {
        var accessToken = await MintAccessTokenAsync();
        var response = await GetUserinfoAsync(accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
    }

    [Fact]
    public async Task TokenError_CarriesNoStore()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = "nope" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    // ── #323 — PAR answers a client-auth failure with a challenge ────

    [Fact]
    public async Task Par_ClientAuthenticationFailure_CarriesChallenge()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/par")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["redirect_uri"] = ProtocolTestHost.SpaRedirectUri,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{ProtocolTestHost.MachineClientId}:definitely-the-wrong-secret")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
        Assert.Contains("realm=\"par\"", challenge.Parameter);
        Assert.Contains("error=\"invalid_client\"", challenge.Parameter);
    }

    // ── #237 — PAR is an anonymous write and is throttled ────────────

    /// <summary>
    /// A public client authenticates at PAR on a bare client_id, and every accepted request persists
    /// a grant row. 300/min per client; the budget is generous because it bounds a flood, not traffic.
    /// Also pins that the Protocol package now brings its own IRateLimiter — the limit used to apply
    /// only in a host that happened to register one.
    /// </summary>
    [Fact]
    public async Task Par_IsRateLimitedPerClient()
    {
        HttpResponseMessage? refused = null;
        for (var i = 0; i < 320; i++)
        {
            var response = await PostParAsync();
            if (response.StatusCode != HttpStatusCode.Created)
            {
                refused = response;
                break;
            }
        }

        Assert.NotNull(refused);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused!.StatusCode);
        var json = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("temporarily_unavailable", json.GetProperty("error").GetString());
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostParAsync() =>
        _client.PostAsync("/connect/par", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ProtocolTestHost.SpaClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = ProtocolTestHost.SpaRedirectUri,
            ["scope"] = "openid",
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ["code_challenge_method"] = "S256",
        }));

    private async Task<HttpResponseMessage> GetUserinfoAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _client.SendAsync(request);
    }

    /// <summary>Full authorization-code flow so the token is a real one with jti, aud and scope.</summary>
    private async Task<string> MintAccessTokenAsync()
    {
        var login = await _client.GetAsync("/test-login");
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authorizeUrl =
            $"/connect/authorize?client_id={ProtocolTestHost.SpaClientId}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(ProtocolTestHost.SpaRedirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid profile email")}&state=xyz" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        var authorizeResponse = await _client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Found, authorizeResponse.StatusCode);
        var code = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"]!;

        var tokenResponse = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ProtocolTestHost.SpaClientId,
            ["code"] = code,
            ["redirect_uri"] = ProtocolTestHost.SpaRedirectUri,
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));
    }
}

/// <summary>
/// The Server host's share of the same re-run: the device endpoint's credential source and throttle,
/// the 401s that never named a scheme, the consent denial that never carried <c>iss</c>, and the
/// admin-minted token pair whose access token no revocation could reach.
/// </summary>
public sealed class ServerHostOAuthResidualTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        var admin = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId);
        admin!.AllowedGrantTypes = ["client_credentials", GrantTypes.DeviceCode];
        admin.AllowedScopes = ["openid", "profile", AuthagonalTestFactory.AdminScope];
        await _factory.ClientStore.UpsertAsync(admin);

        // RFC 8628's own shape: an inputs-constrained device that cannot hold a secret, so reaching
        // the endpoint needs nothing but a client_id anyone can read off the wire.
        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = PublicDeviceClientId,
            ClientName = "Living-room TV",
            RequireClientSecret = false,
            RequirePkce = false,
            AllowedGrantTypes = [GrantTypes.DeviceCode],
            AllowedScopes = ["openid"],
        });
    }

    private const string PublicDeviceClientId = "public-device-client";

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // ── #223 / #237 — device authorization client authentication ────

    /// <summary>
    /// client_secret_basic is the first method discovery advertises and the default for most OAuth
    /// libraries; this endpoint read the form and nothing else, so it was the one endpoint a
    /// Basic-authenticating confidential client could not use.
    /// </summary>
    [Fact]
    public async Task DeviceAuthorization_HttpBasic_IsAccepted()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/deviceauthorization")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = "openid profile" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{AuthagonalTestFactory.AdminClientId}:{AuthagonalTestFactory.AdminClientSecret}")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("device_code").GetString()));
    }

    [Fact]
    public async Task DeviceAuthorization_WrongBasicSecret_IsRefusedWithChallenge()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/deviceauthorization")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = "openid" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{AuthagonalTestFactory.AdminClientId}:wrong")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
        Assert.Contains("error=\"invalid_client\"", challenge.Parameter);
    }

    /// <summary>
    /// Every accepted request persists two grant rows, and a device client may be public, so the
    /// endpoint is an anonymous write. 120/min per client; the 121st is refused.
    /// </summary>
    [Fact]
    public async Task DeviceAuthorization_IsRateLimitedPerClient()
    {
        HttpResponseMessage? refused = null;
        for (var i = 0; i < 130; i++)
        {
            var response = await PostDeviceAuthorizationAsync();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                refused = response;
                break;
            }
        }

        Assert.NotNull(refused);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused!.StatusCode);
        var json = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("temporarily_unavailable", json.GetProperty("error").GetString());
    }

    // ── #323 — the other 401s that authenticate a client ────────────

    [Fact]
    public async Task Introspection_ClientAuthenticationFailure_CarriesChallenge()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "whatever" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AuthagonalTestFactory.AdminClientId}:wrong")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
        Assert.Contains("realm=\"introspect\"", challenge.Parameter);
    }

    [Fact]
    public async Task Revocation_ClientAuthenticationFailure_CarriesChallenge()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/revocation")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "whatever" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AuthagonalTestFactory.AdminClientId}:wrong")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
        Assert.Contains("realm=\"revocation\"", challenge.Parameter);
    }

    // ── #217 — introspection and userinfo caching headers ───────────

    [Fact]
    public async Task Introspection_Response_CarriesNoStore()
    {
        var accessToken = await _factory.GetAdminTokenAsync(_client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = accessToken }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{AuthagonalTestFactory.AdminClientId}:{AuthagonalTestFactory.AdminClientSecret}")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
    }

    // ── #339 — the admin-minted pair is revocable ───────────────────

    /// <summary>
    /// Revoking a refresh token revokes the access tokens minted under it, which works by reading the
    /// jti list off the grant. The admin mint paired two single-token calls, so it wrote a grant with
    /// an empty list and revocation reached nothing.
    /// </summary>
    [Fact]
    public async Task AdminMintedPair_RevokingRefreshToken_RevokesItsAccessToken()
    {
        var adminToken = await _factory.GetAdminTokenAsync(_client);
        var user = await _factory.SeedTestUserAsync(email: "impersonated@example.com");

        var mintRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/token?clientId={AuthagonalTestFactory.TestClientId}&userId={user.Id}" +
            $"&scopes={Uri.EscapeDataString("openid offline_access")}");
        mintRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var mintResponse = await _client.SendAsync(mintRequest);
        Assert.Equal(HttpStatusCode.OK, mintResponse.StatusCode);
        var minted = await mintResponse.Content.ReadFromJsonAsync<JsonElement>();

        var accessJti = DecodeJwtPayload(minted.GetProperty("access_token").GetString()!)
            .GetProperty("jti").GetString()!;
        var refreshToken = minted.GetProperty("refresh_token").GetString()!;

        Assert.False(await _factory.RevokedTokenStore.IsRevokedAsync(accessJti));

        var revokeResponse = await _client.PostAsync("/connect/revocation", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = AuthagonalTestFactory.TestClientId,
                ["token"] = refreshToken,
                ["token_type_hint"] = "refresh_token",
            }));
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        Assert.True(await _factory.RevokedTokenStore.IsRevokedAsync(accessJti));
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostDeviceAuthorizationAsync() =>
        _client.PostAsync("/connect/deviceauthorization", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = PublicDeviceClientId,
            ["scope"] = "openid",
        }));

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));
    }
}
