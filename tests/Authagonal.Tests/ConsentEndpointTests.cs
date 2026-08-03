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

    /// <summary>The default shape: <c>RequireConsent</c> is false unless dynamic registration set it.</summary>
    private const string NoConsentClientId = "no-consent-client";

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

        // The same client with RequireConsent left at its default. This is what every admin-created and
        // config-seeded client looks like, and it is the one for which prompt=consent did nothing at all.
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = NoConsentClientId,
            ClientName = "No-Consent SPA",
            RequireClientSecret = false,
            RequirePkce = true,
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
        // Signed in first: the consent screen is only ever rendered to an authenticated user, and
        // anonymous access made this a client-enumeration oracle over the whole registry.
        await LoginAsync();
        var (_, challenge) = GeneratePkce();
        await BeginConsentFlowAsync(challenge);

        // No `scope` parameter: the scopes come from the offer the authorize request above recorded.
        var response = await _client.GetAsync($"/consent/info?client_id={ConsentClientId}");

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
        await LoginAsync();

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
        var authorizeUrl = await BeginConsentFlowAsync(challenge);

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
        var (_, challenge) = GeneratePkce();
        var returnUrl = await BeginConsentFlowAsync(challenge);

        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "profile", "sneaky-admin-scope" },
            returnUrl,
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

        // RFC 9207 iss. Discovery advertises authorization_response_iss_parameter_supported
        // unconditionally, and this is the authorization error an interactive OP emits most often —
        // a client strict enough to require iss (the reason to ask for it) rejected a genuine denial
        // as a suspected mix-up while every other error redirect carried it.
        Assert.Equal(AuthagonalTestFactory.TestIssuer,
            System.Web.HttpUtility.ParseQueryString(redirectUri.Query)["iss"]);

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
    // The screen and the grant are bound to a pending authorization request
    // -----------------------------------------------------------------------

    /// <summary>
    /// With no pending authorization request, the consent screen has nothing to render.
    /// </summary>
    /// <remarks>
    /// This endpoint used to build its whole answer from the caller's own <c>client_id</c> and <c>scope</c>
    /// query parameters, so a crafted link rendered the IdP's own consent card — its origin, its styling, a
    /// real registered client's name, description and logo — above an attacker-chosen permission list.
    /// </remarks>
    [Fact]
    public async Task ConsentInfo_WithNoPendingRequest_IsRefusedAndDisclosesNothing()
    {
        await LoginAsync();

        var response = await _client.GetAsync($"/consent/info?client_id={ConsentClientId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("no_pending_consent_request", body, StringComparison.Ordinal);
        // Not even the client's display name: this was reconnaissance for a consent-phishing page.
        Assert.DoesNotContain("Consent SPA", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A consent POST with no offer behind it writes no grant.
    /// </summary>
    /// <remarks>
    /// The offer record was read but treated as optional, so this endpoint would write a real five-year
    /// consent grant for any (subject, client) pair on request — the half of the crafted-link problem that
    /// survives closing the browser.
    /// </remarks>
    [Fact]
    public async Task ConsentApprove_WithNoPendingRequest_IsRefusedAndStoresNothing()
    {
        var user = await LoginAsync();

        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "profile" },
            returnUrl = "/somewhere",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await _factory.GrantStore.GetAsync($"consent:{user.Id}:{ConsentClientId}"));
    }

    /// <summary>
    /// The offer is single-use: replaying the same approval writes nothing further.
    /// </summary>
    [Fact]
    public async Task ConsentApprove_ReplayedAfterSuccess_IsRefused()
    {
        await LoginAsync();
        var (_, challenge) = GeneratePkce();
        var returnUrl = await BeginConsentFlowAsync(challenge);

        var body = new { clientId = ConsentClientId, decision = "approve", scopes = new[] { "openid" }, returnUrl };

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/consent", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/consent", body)).StatusCode);
    }

    /// <summary>
    /// A scope the user was never shown cannot be granted by hand-editing the POST body.
    /// </summary>
    /// <remarks>
    /// The granted set was filtered against the client's <c>AllowedScopes</c> only, so any scope the client
    /// was registered for could be recorded as consented without ever appearing on the screen — including
    /// one the role-entitlement filter had just dropped from this request.
    /// </remarks>
    [Fact]
    public async Task ConsentApprove_CannotGrantAScopeThatWasNotOffered()
    {
        var user = await LoginAsync();
        var (_, challenge) = GeneratePkce();

        // The authorize request offers openid+profile. `email` is registered on the client but not offered.
        var returnUrl = await BeginConsentFlowAsync(challenge);

        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "email" },
            returnUrl,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var grant = await _factory.GrantStore.GetAsync($"consent:{user.Id}:{ConsentClientId}");
        Assert.NotNull(grant);
        Assert.Contains("openid", grant.Data);
        Assert.DoesNotContain("email", grant.Data);
    }

    // -----------------------------------------------------------------------
    // prompt=consent
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>prompt=consent</c> reaches the consent screen even when the client does not require consent.
    /// </summary>
    /// <remarks>
    /// <c>AuthorizeRequestSupport.Validate</c> admits <c>prompt=consent</c> deliberately — an unrecognised
    /// prompt value is refused right beside it — but <c>DemandsConsent</c> was read at exactly one place,
    /// inside <c>if (client.RequireConsent)</c>. That property defaults to false and only dynamic
    /// registration sets it, so for every admin-created and config-seeded client the parameter was parsed,
    /// accepted, and dropped: no screen, and no <c>consent_required</c> either.
    /// </remarks>
    [Fact]
    public async Task Authorize_PromptConsent_ShowsTheScreenForAClientThatDoesNotRequireConsent()
    {
        await LoginAsync();
        var (_, challenge) = GeneratePkce();

        // The control first: without the parameter this client issues a code with no screen at all, so the
        // assertion below is about prompt=consent and nothing else.
        var control = await _client.GetAsync(BuildAuthorizeUrl(challenge, clientId: NoConsentClientId));
        Assert.StartsWith(ConsentRedirectUri, control.Headers.Location!.ToString());

        var response = await _client.GetAsync(
            BuildAuthorizeUrl(challenge, clientId: NoConsentClientId, extra: "&prompt=consent"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login/consent?", response.Headers.Location!.ToString());
    }

    /// <summary>
    /// Having shown the screen once, <c>prompt=consent</c> does not demand it again on the return trip.
    /// </summary>
    /// <remarks>
    /// The consent POST sends the user-agent back to the same authorize URL with <c>prompt</c> still on it.
    /// An unconditional re-prompt is therefore an infinite redirect loop between the two endpoints — which
    /// is what a RequireConsent client asking for prompt=consent already got, before this was single-use.
    /// </remarks>
    [Fact]
    public async Task Authorize_PromptConsent_DoesNotLoopAfterTheUserDecides()
    {
        await LoginAsync();
        var (_, challenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(challenge, extra: "&prompt=consent");

        var first = await _client.GetAsync(authorizeUrl);
        Assert.StartsWith("/login/consent?", first.Headers.Location!.ToString());

        var approve = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "profile" },
            returnUrl = authorizeUrl,
        });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // Same URL, prompt included: the decision has been made, so this must issue a code.
        var second = await _client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(ConsentRedirectUri, second.Headers.Location!.ToString());

        // ...and the demand is single-use, so a NEW prompt=consent request asks again.
        var third = await _client.GetAsync(authorizeUrl);
        Assert.StartsWith("/login/consent?", third.Headers.Location!.ToString());
    }

    // -----------------------------------------------------------------------
    // GET /consent/grants
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConsentGrants_ListsOnlyCallingSubjectsConsentGrants()
    {
        var user = await LoginAsync();
        var (_, challenge) = GeneratePkce();
        var returnUrl = await BeginConsentFlowAsync(challenge);

        // The caller's consent grant, via the real approve path
        var approve = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid", "profile" },
            returnUrl,
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
    public async Task ConsentInfo_Unauthenticated_DisclosesNothing()
    {
        // The registry is not public. Names, descriptions, logo and home URIs are reconnaissance for
        // a consent-phishing page impersonating a client the user has seen before.
        var response = await _client.GetAsync($"/consent/info?client_id={ConsentClientId}");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Consent SPA", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
        var (_, challenge) = GeneratePkce();
        var returnUrl = await BeginConsentFlowAsync(challenge);

        // Caller's consent for the client, via the real approve path
        var approve = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "approve",
            scopes = new[] { "openid" },
            returnUrl,
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

    /// <summary>
    /// Drives the authorize request a real consent screen is always reached from, and returns that URL.
    /// </summary>
    /// <remarks>
    /// Both halves of the consent surface require a pending offer now: <c>GET /consent/info</c> renders from
    /// it and <c>POST /consent</c> refuses without it. These tests used to POST straight to <c>/consent</c>
    /// with no authorization request behind it, which is precisely the shape a crafted consent link has —
    /// so going through authorize is not ceremony, it is the thing that makes the test faithful.
    /// </remarks>
    private async Task<string> BeginConsentFlowAsync(string challenge, string state = "test")
    {
        var authorizeUrl = BuildAuthorizeUrl(challenge, state);
        var response = await _client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login/consent?", response.Headers.Location!.ToString());
        return authorizeUrl;
    }

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

    private static string BuildAuthorizeUrl(
        string challenge,
        string state = "test",
        string clientId = ConsentClientId,
        string extra = "")
        => $"/connect/authorize?client_id={clientId}" +
           $"&redirect_uri={Uri.EscapeDataString(ConsentRedirectUri)}" +
           $"&response_type=code&scope=openid+profile" +
           $"&state={state}&code_challenge={challenge}&code_challenge_method=S256{extra}";

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
