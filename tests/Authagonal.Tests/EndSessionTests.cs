using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

public sealed class EndSessionTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // Both tests below previously asserted that a bare GET (and a bare POST) ended the session
    // outright. That is the behaviour OIDC RP-Initiated Logout 1.0 §2 forbids without an
    // id_token_hint matching the session — and because the session cookie is SameSite=Lax, which
    // does ride a cross-site top-level GET, it was also a logout-CSRF sink (§6 names it as a DoS
    // vector). The confirmation interstitial closed it. The GET test then failed outright (it
    // parses the HTML page as JSON) and the POST test kept passing vacuously, because it asserted
    // only 200 and the interstitial is also a 200. Both now pin the interstitial itself.

    [Fact]
    public async Task EndSession_Get_WithoutIdTokenHint_ConfirmsAndLeavesSessionIntact()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/session")).StatusCode);

        var response = await _client.GetAsync("/connect/endsession");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("logout_confirm", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The whole point of the branch: it has no side effects, so a cross-site navigation cannot
        // sign the user out.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task EndSession_Post_WithConfirmationToken_SignsOut()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // Drive the real interstitial rather than a fabricated token, so the test breaks if the
        // form stops carrying a usable confirmation.
        var confirmation = await _client.GetAsync("/connect/endsession");
        var token = ConfirmationTokenFrom(await confirmation.Content.ReadAsStringAsync());

        var response = await _client.PostAsync("/connect/endsession", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["logout_confirm"] = token }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(json.GetProperty("message").GetString());

        Assert.NotEqual(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task EndSession_Post_WithoutConfirmationToken_DoesNotSignOut()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var response = await _client.PostAsync("/connect/endsession",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        // 200, but the interstitial — which is why asserting the status code alone said nothing.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task EndSession_Post_WithTamperedConfirmationToken_DoesNotSignOut()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var confirmation = await _client.GetAsync("/connect/endsession");
        var token = ConfirmationTokenFrom(await confirmation.Content.ReadAsStringAsync());

        var response = await _client.PostAsync("/connect/endsession", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["logout_confirm"] = token[..^4] + "AAAA" }));

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/session")).StatusCode);
    }

    /// <summary>Pulls the hidden <c>logout_confirm</c> value out of the interstitial's form.</summary>
    private static string ConfirmationTokenFrom(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"logout_confirm\" value=\"([^\"]+)\"");
        Assert.True(match.Success, "The confirmation page did not render a logout_confirm token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Fact]
    public async Task EndSession_WithValidIdTokenHintAndRedirectUri_Redirects()
    {
        // Login and get tokens via PKCE flow
        var user = await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope=openid+profile+email" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = verifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });
        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var idToken = tokens.GetProperty("id_token").GetString()!;

        // End session with id_token_hint + registered post_logout_redirect_uri
        var endSessionUrl = $"/connect/endsession?id_token_hint={Uri.EscapeDataString(idToken)}" +
            $"&post_logout_redirect_uri={Uri.EscapeDataString("https://app.test")}" +
            $"&state=logout123";

        var response = await _client.GetAsync(endSessionUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://app.test", location);
        Assert.Contains("state=logout123", location);
    }

    // ---------------------------------------------------------------------------------------------
    // F331 — RP-Initiated Logout 1.0 §3 requires an EXACT match against a registered value
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The check was a whole-string OrdinalIgnoreCase Contains, so a registration carrying a
    /// mixed-case path or query also admitted every case variant of it. Scheme and host stay
    /// case-insensitive (RFC 3986); path and query must not. The authorization endpoint always got
    /// this right — the two matchers had drifted, and this was the copy that was wrong.
    /// </summary>
    [Theory]
    [InlineData("https://app.test/Logout?tenant=Acme", true)]  // exactly as registered
    [InlineData("https://APP.TEST/Logout?tenant=Acme", true)]  // host case is legitimately insensitive
    [InlineData("https://app.test/logout?tenant=Acme", false)] // path case differs
    [InlineData("https://app.test/Logout?tenant=acme", false)] // query case differs
    public async Task EndSession_PostLogoutRedirectUri_MatchesPathAndQueryCaseSensitively(
        string requested, bool shouldRedirect)
    {
        const string registered = "https://app.test/Logout?tenant=Acme";
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.PostLogoutRedirectUris = [.. client.PostLogoutRedirectUris, registered];
        await _factory.ClientStore.UpsertAsync(client);

        var idToken = await SignInAndGetIdTokenAsync();

        var response = await _client.GetAsync(
            $"/connect/endsession?id_token_hint={Uri.EscapeDataString(idToken)}" +
            $"&post_logout_redirect_uri={Uri.EscapeDataString(requested)}");

        if (shouldRedirect)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("https://", response.Headers.Location!.ToString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>Runs the PKCE flow and returns an id_token usable as an <c>id_token_hint</c>.</summary>
    private async Task<string> SignInAndGetIdTokenAsync()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authResponse = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope=openid+profile+email" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256");
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var tokenResponse = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["code_verifier"] = verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return tokens.GetProperty("id_token").GetString()!;
    }

    [Fact]
    public async Task EndSession_WithUnregisteredRedirectUri_DoesNotRedirect()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // Use a redirect_uri not registered on any client
        var response = await _client.GetAsync(
            "/connect/endsession?post_logout_redirect_uri=https://evil.com/logout");

        // Should not redirect — returns OK with message
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EndSession_NotAuthenticated_StillReturnsOk()
    {
        var response = await _client.GetAsync("/connect/endsession");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IdToken_EmitsSidClaim_WhenCookieHasSessionId()
    {
        // Back-channel logout correlates to a session via the `sid` claim — the cookie
        // sign-in flow mints a fresh sid per login and the ID token must carry it
        // through so RPs can match logout notifications back to their active session.
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope=openid+profile+email+offline_access" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = verifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });
        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var idToken = tokens.GetProperty("id_token").GetString()!;

        var parsed = new JsonWebTokenHandler().ReadJsonWebToken(idToken);
        var sid = parsed.GetClaim("sid")?.Value;
        Assert.False(string.IsNullOrEmpty(sid));

        // Refresh must preserve the sid so logout tokens remain correlatable after
        // the app has rotated its tokens many times.
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });
        var refreshed = await _client.PostAsync("/connect/token", refreshForm);
        var refreshedTokens = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var refreshedIdToken = refreshedTokens.GetProperty("id_token").GetString()!;
        var refreshedSid = new JsonWebTokenHandler().ReadJsonWebToken(refreshedIdToken).GetClaim("sid")?.Value;
        Assert.Equal(sid, refreshedSid);
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
