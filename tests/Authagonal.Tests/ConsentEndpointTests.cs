using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Integration tests for the consent surface: GET /consent/info, POST /consent
/// (approve/deny), GET /consent/grants, DELETE /consent/grants/{clientId} — plus the
/// authorize-endpoint integration for a RequireConsent client (redirect to the consent
/// page, then code issuance once consent is persisted).
/// </summary>
public sealed class ConsentEndpointTests : IAsyncLifetime
{
    private const string ConsentClientId = "consent-client";
    private const string ConsentRedirectUri = "https://consent.test/callback";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        // A PKCE client that requires consent
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = ConsentClientId,
            ClientName = "Consent SPA",
            Description = "Client that requires user consent",
            RequireClientSecret = false,
            RequirePkce = true,
            RequireConsent = true,
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = [ConsentRedirectUri],
            AllowedScopes = ["openid", "profile", "email"],
            AccessTokenLifetimeSeconds = 3600,
        });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // GET /consent/info
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConsentInfo_ReturnsClientMetadataAndRequestedScopes()
    {
        var response = await _client.GetAsync(
            $"/consent/info?client_id={ConsentClientId}&scope=openid%20profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ConsentClientId, json.GetProperty("clientId").GetString());
        Assert.Equal("Consent SPA", json.GetProperty("clientName").GetString());
        var scopes = json.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal(["openid", "profile"], scopes);
    }

    [Fact]
    public async Task ConsentInfo_UnknownClient_Returns404()
    {
        var response = await _client.GetAsync("/consent/info?client_id=no-such-client");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client_not_found", json.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Authorize + consent flow
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Authorize_RequireConsentClient_WithoutConsent_RedirectsToConsentPage()
    {
        await LoginAsync();
        var (_, challenge) = GeneratePkce();

        var response = await _client.GetAsync(BuildAuthorizeUrl(challenge));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/login/consent?", location);
        Assert.Contains($"client_id={ConsentClientId}", location);
    }

    [Fact]
    public async Task ConsentApprove_PersistsGrant_AndAuthorizeIssuesCode()
    {
        var user = await LoginAsync();
        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(challenge);

        // Approve consent for the requested scopes
        var approveResponse = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "profile" },
            returnUrl = authorizeUrl,
        });

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approveJson = await approveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(authorizeUrl, approveJson.GetProperty("redirect").GetString());

        // Consent grant persisted under consent:{sub}:{client}
        var grant = await _factory.GrantStore.GetAsync($"consent:{user.Id}:{ConsentClientId}");
        Assert.NotNull(grant);
        Assert.Equal("consent", grant.Type);
        Assert.Equal(user.Id, grant.SubjectId);
        Assert.Equal(ConsentClientId, grant.ClientId);

        // Authorize now proceeds straight to the client redirect with a code
        var authResponse = await _client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authResponse.StatusCode);
        var location = authResponse.Headers.Location!;
        Assert.StartsWith(ConsentRedirectUri, location.ToString());
        var code = HttpUtility.ParseQueryString(location.Query)["code"];
        Assert.False(string.IsNullOrEmpty(code));

        // ...and the code exchanges for tokens
        var tokenResponse = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = ConsentRedirectUri,
                ["code_verifier"] = verifier,
                ["client_id"] = ConsentClientId,
            }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task ConsentApprove_FiltersScopesOutsideClientAllowedScopes()
    {
        var user = await LoginAsync();

        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "profile", "sneaky-admin-scope" },
            returnUrl = "/somewhere",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var grant = await _factory.GrantStore.GetAsync($"consent:{user.Id}:{ConsentClientId}");
        Assert.NotNull(grant);
        Assert.Contains("openid", grant.Data);
        Assert.Contains("profile", grant.Data);
        Assert.DoesNotContain("sneaky-admin-scope", grant.Data);
    }

    [Fact]
    public async Task ConsentApprove_UnknownClient_Returns404()
    {
        await LoginAsync();

        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = "no-such-client",
            decision = "approve",
            scopes = new[] { "openid" },
            returnUrl = "/somewhere",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client_not_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConsentDeny_RedirectsBackToClientWithAccessDenied_AndStoresNothing()
    {
        var user = await LoginAsync();
        var (_, challenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(challenge, state: "abc123");

        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "deny",
            returnUrl = authorizeUrl,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var redirect = json.GetProperty("redirect").GetString()!;
        // Current behavior: the endpoint builds the redirect with UriBuilder, whose ToString()
        // renders the (explicit) default port — "https://consent.test:443/callback...". Semantically
        // equivalent to the registered redirect URI, so compare structurally.
        var redirectUri = new Uri(redirect);
        Assert.Equal(new Uri(ConsentRedirectUri).GetLeftPart(UriPartial.Path),
            redirectUri.GetLeftPart(UriPartial.Path));
        Assert.Contains("error=access_denied", redirect);
        Assert.Contains("state=abc123", redirect);

        // No consent grant persisted on deny
        var grant = await _factory.GrantStore.GetAsync($"consent:{user.Id}:{ConsentClientId}");
        Assert.Null(grant);
    }

    [Fact]
    public async Task ConsentDeny_WithoutReturnUrl_RedirectsHome()
    {
        await LoginAsync();

        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "deny",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/", json.GetProperty("redirect").GetString());
    }

    [Fact]
    public async Task ConsentPost_Unauthenticated_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid" },
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // GET /consent/grants
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConsentGrants_ListsOnlyCallingSubjectsConsentGrants()
    {
        var user = await LoginAsync();

        // The caller's consent grant, via the real approve path
        var approve = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "profile" },
            returnUrl = "/somewhere",
        });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // Another user's consent grant — must NOT appear in the caller's list
        await SeedConsentGrantAsync("other-user-id", AuthagonalTestFactory.TestClientId);

        // A non-consent grant for the caller — must be filtered out by type
        await _factory.GrantStore.StoreAsync(new PersistedGrant
        {
            Key = Guid.NewGuid().ToString("N"),
            Type = "refresh_token",
            SubjectId = user.Id,
            ClientId = ConsentClientId,
            Data = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });

        var response = await _client.GetAsync("/consent/grants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal(ConsentClientId, item.GetProperty("clientId").GetString());
        Assert.Equal("Consent SPA", item.GetProperty("clientName").GetString());
        var scopes = item.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("openid", scopes);
        Assert.Contains("profile", scopes);
    }

    [Fact]
    public async Task ConsentGrants_Unauthenticated_Returns401()
    {
        var response = await _client.GetAsync("/consent/grants");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // DELETE /consent/grants/{clientId}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteConsentGrant_RemovesOnlyTheCallersGrantForThatClient()
    {
        var user = await LoginAsync();

        // Caller's consent for the client, via the real approve path
        var approve = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid" },
            returnUrl = "/somewhere",
        });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // Another user's consent for the SAME client — must survive the caller's revocation
        await SeedConsentGrantAsync("other-user-id", ConsentClientId);

        var response = await _client.DeleteAsync($"/consent/grants/{ConsentClientId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Null(await _factory.GrantStore.GetAsync($"consent:{user.Id}:{ConsentClientId}"));
        Assert.NotNull(await _factory.GrantStore.GetAsync($"consent:other-user-id:{ConsentClientId}"));
    }

    [Fact]
    public async Task DeleteConsentGrant_Unauthenticated_Returns401()
    {
        var response = await _client.DeleteAsync($"/consent/grants/{ConsentClientId}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<AuthUser> LoginAsync(
        string email = "test@example.com", string password = "Test1234!")
    {
        var user = await _factory.SeedTestUserAsync(email, password);
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return user;
    }

    private Task SeedConsentGrantAsync(string subjectId, string clientId)
        => _factory.GrantStore.StoreAsync(new PersistedGrant
        {
            Key = $"consent:{subjectId}:{clientId}",
            Type = "consent",
            SubjectId = subjectId,
            ClientId = clientId,
            Data = """{"scopes":["openid"],"consentedAt":"2026-01-01T00:00:00+00:00"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(5),
        });

    private static string BuildAuthorizeUrl(string challenge, string state = "test")
        => $"/connect/authorize?client_id={ConsentClientId}" +
           $"&redirect_uri={Uri.EscapeDataString(ConsentRedirectUri)}" +
           $"&response_type=code&scope=openid+profile" +
           $"&state={state}&code_challenge={challenge}&code_challenge_method=S256";

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
