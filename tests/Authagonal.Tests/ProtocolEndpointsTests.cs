using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Integration coverage for the published Authagonal.Protocol surface on its own —
/// MapAuthagonalProtocolEndpoints + AddAuthagonalProtocol in a host with no
/// Authagonal.Server components (the bullclip consumer shape).
/// </summary>
public sealed class ProtocolEndpointsTests : IAsyncLifetime
{
    private readonly ProtocolTestHost _host = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _host.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    // ── Discovery / JWKS ────────────────────────────────────────────

    [Fact]
    public async Task Discovery_AdvertisesProtocolSurfaceOnly()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ProtocolTestHost.TestIssuer, json.GetProperty("issuer").GetString());
        Assert.Equal($"{ProtocolTestHost.TestIssuer}/connect/authorize", json.GetProperty("authorization_endpoint").GetString());
        Assert.Equal($"{ProtocolTestHost.TestIssuer}/connect/token", json.GetProperty("token_endpoint").GetString());
        Assert.Equal($"{ProtocolTestHost.TestIssuer}/.well-known/openid-configuration/jwks", json.GetProperty("jwks_uri").GetString());
        Assert.Equal($"{ProtocolTestHost.TestIssuer}/connect/par", json.GetProperty("pushed_authorization_request_endpoint").GetString());
        Assert.Equal("S256", json.GetProperty("code_challenge_methods_supported")[0].GetString());

        // Server-only surface must not leak into the Protocol document — the shared
        // DiscoveryResponse omits unset optional members instead of emitting nulls.
        Assert.False(json.TryGetProperty("revocation_endpoint", out _));
        Assert.False(json.TryGetProperty("end_session_endpoint", out _));
        Assert.False(json.TryGetProperty("device_authorization_endpoint", out _));
        Assert.False(json.TryGetProperty("backchannel_logout_supported", out _));
        Assert.False(json.TryGetProperty("require_pushed_authorization_requests", out _));

        var grantTypes = json.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.DoesNotContain("urn:ietf:params:oauth:grant-type:device_code", grantTypes);
    }

    [Fact]
    public async Task Jwks_PublishesEcSigningKey()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration/jwks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = json.GetProperty("keys");
        Assert.True(keys.GetArrayLength() >= 1);

        var key = keys[0];
        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("ES256", key.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrEmpty(key.GetProperty("kid").GetString()));
        Assert.False(string.IsNullOrEmpty(key.GetProperty("x").GetString()));
        Assert.False(string.IsNullOrEmpty(key.GetProperty("y").GetString()));

        // RSA members are omitted for an EC key, not emitted as nulls.
        Assert.False(key.TryGetProperty("n", out _));
        Assert.False(key.TryGetProperty("e", out _));
    }

    // ── Authorization-code flow ─────────────────────────────────────

    [Fact]
    public async Task AuthorizationCodeFlow_WithPkce_IssuesTokens()
    {
        await SignInAsync();
        var (verifier, challenge) = NewPkcePair();

        var code = await AuthorizeAndExtractCodeAsync(AuthorizeUrl(challenge));
        var tokens = await RedeemCodeAsync(code, verifier);

        var accessToken = tokens.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrEmpty(accessToken));
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("id_token").GetString()));
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refresh_token").GetString()));
        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());

        var payload = DecodeJwtPayload(accessToken!);
        Assert.Equal(ProtocolTestHost.TestIssuer, payload.GetProperty("iss").GetString());
        Assert.Equal(ProtocolTestHost.TestSubjectId, payload.GetProperty("sub").GetString());
    }

    [Fact]
    public async Task RefreshTokenGrant_RotatesRefreshToken()
    {
        await SignInAsync();
        var (verifier, challenge) = NewPkcePair();
        var code = await AuthorizeAndExtractCodeAsync(AuthorizeUrl(challenge));
        var first = await RedeemCodeAsync(code, verifier);
        var firstRefresh = first.GetProperty("refresh_token").GetString()!;

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ProtocolTestHost.SpaClientId,
            ["refresh_token"] = firstRefresh,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(second.GetProperty("access_token").GetString()));
        Assert.NotEqual(firstRefresh, second.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public async Task Authorize_Unauthenticated_ChallengesHostScheme()
    {
        var (_, challenge) = NewPkcePair();
        var response = await _client.GetAsync(AuthorizeUrl(challenge));

        // Cookie challenge → redirect to the host's login page with a returnUrl.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("/host-login", location.AbsolutePath);
        var returnUrl = HttpUtility.ParseQueryString(location.Query)["ReturnUrl"];
        Assert.Contains("/connect/authorize", returnUrl);
    }

    // ── PAR flow ────────────────────────────────────────────────────

    [Fact]
    public async Task PushedAuthorizationRequest_FullFlow_IssuesTokens()
    {
        await SignInAsync();
        var (verifier, challenge) = NewPkcePair();

        var parResponse = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ProtocolTestHost.SpaClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = ProtocolTestHost.SpaRedirectUri,
            ["scope"] = "openid profile offline_access",
            ["state"] = "par-state",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        }));

        Assert.Equal(HttpStatusCode.Created, parResponse.StatusCode);
        var parJson = await parResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestUri = parJson.GetProperty("request_uri").GetString()!;
        Assert.StartsWith("urn:ietf:params:oauth:request_uri:", requestUri);

        var authorizeUrl = $"/connect/authorize?client_id={ProtocolTestHost.SpaClientId}&request_uri={Uri.EscapeDataString(requestUri)}";
        var code = await AuthorizeAndExtractCodeAsync(authorizeUrl, expectedState: "par-state");
        var tokens = await RedeemCodeAsync(code, verifier);
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("access_token").GetString()));
    }

    // ── Userinfo ────────────────────────────────────────────────────

    /// <summary>
    /// Userinfo returns the scope-released standard claims, not just <c>sub</c>.
    /// </summary>
    /// <remarks>
    /// This host's userinfo answers from the presented ACCESS token by design — "userinfo returns whatever
    /// claims the access token carried … relying parties should call userinfo for a snapshot, not fresh
    /// re-resolution" — and it scope-gates what it copies with <c>CopyIfScoped("email", …)</c> and
    /// <c>CopyIfScoped("profile", …)</c>. But <c>MintAccessTokenAsync</c> never wrote any of those: its claim
    /// set was <c>client_id</c>, <c>scope</c>, <c>jti</c>, <c>iat</c>, <c>sub</c>, <c>roles</c>, <c>groups</c>
    /// and scope-gated custom attributes. So there was nothing to copy — the endpoint returned <c>sub</c>
    /// alone, while discovery advertised email, name, given_name, family_name, phone_number and org_id.
    /// <para>
    /// Those <c>CopyIfScoped</c> lines only make sense if the claims are on the token, which is the design
    /// this restores by sharing one §5.4 projection between the id_token and the access token.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Userinfo_ReturnsTheScopeReleasedClaims()
    {
        await SignInAsync();
        var (verifier, challenge) = NewPkcePair();

        var code = await AuthorizeAndExtractCodeAsync(AuthorizeUrl(challenge, "openid email"));
        var tokens = await RedeemCodeAsync(code, verifier);
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ProtocolTestHost.TestSubjectId, claims.GetProperty("sub").GetString());
        Assert.Equal(ProtocolTestHost.TestEmail, claims.GetProperty("email").GetString());
    }

    /// <summary>
    /// A token without the <c>email</c> scope gets no email — from the token or from userinfo.
    /// </summary>
    /// <remarks>
    /// The control, and the one that matters most: putting the §5.4 sets on the access token must not release
    /// them beyond the scopes the client was granted, because an access token goes to resource servers.
    /// </remarks>
    [Fact]
    public async Task Userinfo_WithoutTheEmailScope_ReleasesNoEmail()
    {
        await SignInAsync();
        var (verifier, challenge) = NewPkcePair();

        var code = await AuthorizeAndExtractCodeAsync(AuthorizeUrl(challenge, "openid"));
        var tokens = await RedeemCodeAsync(code, verifier);
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        // Not on the token itself.
        var payload = DecodeJwtPayload(accessToken);
        Assert.False(payload.TryGetProperty("email", out _));

        // ...and therefore not from userinfo.
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProtocolTestHost.TestSubjectId, claims.GetProperty("sub").GetString());
        Assert.False(claims.TryGetProperty("email", out _));
    }

    // ── Client credentials ──────────────────────────────────────────

    [Fact]
    public async Task ClientCredentials_WithBasicAuth_IssuesToken()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "machine-api",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(ProtocolTestHost.MachineClientId)}:{Uri.EscapeDataString(ProtocolTestHost.MachineClientSecret)}")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var payload = DecodeJwtPayload(json.GetProperty("access_token").GetString()!);
        Assert.Equal(ProtocolTestHost.TestIssuer, payload.GetProperty("iss").GetString());
    }

    [Fact]
    public async Task Token_UnknownClient_Returns401InvalidClient()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "nope",
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client", json.GetProperty("error").GetString());
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async Task SignInAsync()
    {
        var response = await _client.GetAsync("/test-login");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static string AuthorizeUrl(string codeChallenge, string scope = "openid profile offline_access") =>
        $"/connect/authorize?client_id={ProtocolTestHost.SpaClientId}" +
        $"&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(ProtocolTestHost.SpaRedirectUri)}" +
        $"&scope={Uri.EscapeDataString(scope)}" +
        $"&state=xyz" +
        $"&code_challenge={codeChallenge}" +
        $"&code_challenge_method=S256";

    private async Task<string> AuthorizeAndExtractCodeAsync(string authorizeUrl, string expectedState = "xyz")
    {
        var response = await _client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(ProtocolTestHost.SpaRedirectUri, location.ToString(), StringComparison.Ordinal);
        var query = HttpUtility.ParseQueryString(location.Query);
        Assert.Equal(expectedState, query["state"]);

        var code = query["code"];
        Assert.False(string.IsNullOrEmpty(code));
        return code!;
    }

    private async Task<JsonElement> RedeemCodeAsync(string code, string codeVerifier)
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ProtocolTestHost.SpaClientId,
            ["code"] = code,
            ["redirect_uri"] = ProtocolTestHost.SpaRedirectUri,
            ["code_verifier"] = codeVerifier,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static (string Verifier, string Challenge) NewPkcePair()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));
    }
}
