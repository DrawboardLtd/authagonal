using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// End-to-end test of the Authagonal.OidcProvider library: sign in via the cookie
/// scheme, hit /connect/authorize, then exchange the resulting code at /connect/token.
/// </summary>
public sealed class OidcProviderIntegrationTests
{
    [Fact]
    public async Task AuthorizationCodePkceFlow_IssuesAccessIdAndRefreshTokens()
    {
        await using var host = new OidcProviderTestHost();
        var http = host.CreateClient();

        var (verifier, challenge) = GeneratePkce();

        // Build the authorize URL we'll return to after the test-login endpoint.
        var authorizeUrl =
            "/connect/authorize?" +
            "response_type=code" +
            $"&client_id={OidcProviderTestHost.TestClientId}" +
            $"&redirect_uri={HttpUtility.UrlEncode(OidcProviderTestHost.TestRedirectUri)}" +
            "&scope=openid%20profile%20email%20offline_access" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            "&state=state-xyz" +
            "&nonce=nonce-xyz";

        // Sign in via the test-only login endpoint — sets the cookie and redirects back.
        var loginResponse = await http.GetAsync(
            $"/__test/login?sub=alice&returnUrl={HttpUtility.UrlEncode(authorizeUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        // Follow the redirect manually so we can assert on it: the test client's cookie
        // jar already carries the set-cookie header, so the next GET is authenticated.
        var authorizeResponse = await http.GetAsync(loginResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);

        var location = authorizeResponse.Headers.Location!.ToString();
        Assert.StartsWith(OidcProviderTestHost.TestRedirectUri, location);

        var returnedQuery = HttpUtility.ParseQueryString(new Uri(location).Query);
        Assert.Equal("state-xyz", returnedQuery["state"]);
        var code = returnedQuery["code"];
        Assert.False(string.IsNullOrEmpty(code));

        var tokenResponse = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OidcProviderTestHost.TestClientId,
                ["redirect_uri"] = OidcProviderTestHost.TestRedirectUri,
                ["code"] = code!,
                ["code_verifier"] = verifier,
            }));

        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrEmpty(tokens!.AccessToken));
        Assert.False(string.IsNullOrEmpty(tokens.IdToken));
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        Assert.Equal("Bearer", tokens.TokenType, ignoreCase: true);
    }

    [Fact]
    public async Task RefreshTokenGrant_RotatesAndIssuesFreshAccessToken()
    {
        await using var host = new OidcProviderTestHost();
        var http = host.CreateClient();

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl =
            "/connect/authorize?" +
            "response_type=code" +
            $"&client_id={OidcProviderTestHost.TestClientId}" +
            $"&redirect_uri={HttpUtility.UrlEncode(OidcProviderTestHost.TestRedirectUri)}" +
            "&scope=openid%20offline_access" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            "&state=s";

        var login = await http.GetAsync(
            $"/__test/login?sub=alice&returnUrl={HttpUtility.UrlEncode(authorizeUrl)}");
        var authorize = await http.GetAsync(login.Headers.Location);
        var code = HttpUtility.ParseQueryString(new Uri(authorize.Headers.Location!.ToString()).Query)["code"];

        var first = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = OidcProviderTestHost.TestClientId,
            ["redirect_uri"] = OidcProviderTestHost.TestRedirectUri,
            ["code"] = code!,
            ["code_verifier"] = verifier,
        }));
        first.EnsureSuccessStatusCode();
        var firstTokens = (await first.Content.ReadFromJsonAsync<TokenResponse>())!;

        var refreshed = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = OidcProviderTestHost.TestClientId,
            ["refresh_token"] = firstTokens.RefreshToken!,
            ["scope"] = "openid offline_access",
        }));

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshedTokens = (await refreshed.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.False(string.IsNullOrEmpty(refreshedTokens.AccessToken));
        Assert.NotEqual(firstTokens.AccessToken, refreshedTokens.AccessToken);
        Assert.False(string.IsNullOrEmpty(refreshedTokens.RefreshToken));
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Convert.ToBase64String(verifierBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return (verifier, challenge);
    }

    private sealed class TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
