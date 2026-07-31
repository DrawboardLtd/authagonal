using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Protocol details a relying party is entitled to rely on: what discovery promises, what an
/// authorization error carries, and the headers a token and a 401 must have.
/// </summary>
public sealed class ProtocolConformanceTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F100 / F142 / F344 — RFC 9207 iss on error responses
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AuthorizationError_CarriesTheIssuer()
    {
        // The success path has always sent `iss`, and discovery advertises
        // authorization_response_iss_parameter_supported: true — but errors omitted it, so a client
        // that requires iss on every authorization response had to special-case failures, and could
        // not tell which of several authorization servers an error came from. That ambiguity is the
        // mix-up attack the parameter closes.
        var response = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            "&response_type=token&scope=openid&state=xyz");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);

        Assert.NotNull(query["error"]);
        Assert.False(string.IsNullOrEmpty(query["iss"]), "the error redirect carried no iss");
        Assert.Equal("xyz", query["state"]);
    }

    // -----------------------------------------------------------------------
    // F217 — token responses must not be cached
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TokenResponse_IsNoStore()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
                ["scope"] = AuthagonalTestFactory.AdminScope,
            }));

        response.EnsureSuccessStatusCode();

        // RFC 6749 §5.1 is explicit, and the body carries the access, refresh and ID tokens — so any
        // intermediary applying heuristic freshness could retain them.
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
    }

    // -----------------------------------------------------------------------
    // F323 — 401 invalid_client must challenge
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvalidClient_CarriesAWwwAuthenticateChallenge()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = "wrong",
            }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEmpty(response.Headers.WwwAuthenticate);
    }

    // -----------------------------------------------------------------------
    // F341 / F216 — userinfo
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Userinfo_Unauthorized_CarriesABearerChallenge()
    {
        var response = await _client.GetAsync("/connect/userinfo");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h =>
            string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Userinfo_AcceptsPost()
    {
        // OIDC Core §5.3.1 requires both verbs. Only GET was mapped, so a client following the
        // spec's POST form got a 405 from an endpoint that advertises support.
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/userinfo");
        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // F64 / F144 — discovery must describe what the server actually does
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Discovery_AdvertisesNoneAuthMethod()
    {
        var doc = await Discovery();

        // The token endpoint accepts a public client on client_id alone, and dynamic registration
        // issues exactly such clients — so omitting `none` told every SPA and native client that the
        // only way in was a credential they cannot hold.
        var methods = doc.GetProperty("token_endpoint_auth_methods_supported")
            .EnumerateArray().Select(m => m.GetString()).ToArray();
        Assert.Contains("none", methods);
    }

    [Fact]
    public async Task Discovery_AdvertisesBackchannelLogoutSession()
    {
        var doc = await Discovery();

        // The OP puts `sid` in every ID token and Logout Token, which IS session-based back-channel
        // logout. Advertising false told a conforming RP to ignore the sid it was being sent.
        Assert.True(doc.GetProperty("backchannel_logout_session_supported").GetBoolean());
    }

    private async Task<JsonElement> Discovery() =>
        await (await _client.GetAsync("/.well-known/openid-configuration"))
            .Content.ReadFromJsonAsync<JsonElement>();
}
