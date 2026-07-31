using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Authority;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// Composite delegation end to end: a registered agent exchanges a user's token for an
/// attenuated composite token (sub = user, act = agent, authorization_details = the
/// invariant's intersection), gated by standing consent, JIT approvals, and depth caps.
/// </summary>
public sealed class AgenticDelegationTests : IAsyncLifetime
{
    private const string AgentClientId = "agent-client";
    private const string AgentClientSecret = "agent-secret-789";
    private const string SubAgentClientId = "sub-agent-client";
    private const string SubAgentClientSecret = "sub-agent-secret-000";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        foreach (var (id, secret) in new[] { (AgentClientId, AgentClientSecret), (SubAgentClientId, SubAgentClientSecret) })
        {
            await _factory.ClientStore.UpsertAsync(new OAuthClient
            {
                ClientId = id,
                ClientName = $"Agent {id}",
                RequireClientSecret = true,
                ClientSecretHashes = [hasher.HashPassword(secret)],
                AllowedGrantTypes = [GrantTypes.TokenExchange, GrantTypes.ClientCredentials],
                AllowedScopes = ["openid", "profile", "email"],
                AccessTokenLifetimeSeconds = 3600,
            });
        }

        await _factory.AgentProfileStore.UpsertAsync(new AgentProfile
        {
            ClientId = AgentClientId,
            Mode = AgentMode.Both,
            Ceiling = Ceiling(),
            MaxDelegationDepth = 0,
            MaxTokenLifetimeSeconds = 300,
        });
        await _factory.AgentProfileStore.UpsertAsync(new AgentProfile
        {
            ClientId = SubAgentClientId,
            Mode = AgentMode.Delegated,
            Ceiling = Ceiling(),
            MaxDelegationDepth = 0,
            MaxTokenLifetimeSeconds = 300,
        });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // Ceiling: email send is ask-gated, email read is auto, calendar read is auto.
    private static AuthoritySet Ceiling() => AuthoritySet.Of(
        new AuthorityGrant
        {
            Type = "email",
            Actions = ["send", "read"],
            ActionPolicies = new Dictionary<string, ActionPolicy> { ["send"] = ActionPolicy.Ask },
            Constraints = new Dictionary<string, ConstraintValue>
            {
                ["recipient_domains"] = ConstraintValue.Of("@acme.com"),
            },
        },
        new AuthorityGrant { Type = "calendar", Actions = ["read"] });

    [Fact]
    public async Task Exchange_WithoutConsent_IsConsentRequired()
    {
        var primary = await GetPrimaryAccessTokenAsync();

        var response = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
        Assert.Contains("consent_required", body.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Exchange_MintsCompositeToken_WithIntersectedAuthority()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync(AgentClientId);

        var response = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The profile's lifetime cap (300s) clamps below the client/subject lifetimes.
        Assert.InRange(body.GetProperty("expires_in").GetInt32(), 1, 300);

        // Response echoes the granted authority (RFC 9396 §7).
        var granted = body.GetProperty("authorization_details");
        Assert.Equal(JsonValueKind.Array, granted.ValueKind);

        var token = new JsonWebTokenHandler().ReadJsonWebToken(body.GetProperty("access_token").GetString()!);

        // Composite identity: sub stays the user, act names the agent.
        Assert.True(token.TryGetPayloadValue<JsonElement>("act", out var act));
        Assert.Equal(AgentClientId, act.GetProperty("sub").GetString());
        Assert.NotEqual(AgentClientId, token.Subject);

        // The claim carries exactly the intersection: email/read only.
        Assert.True(token.TryGetPayloadValue<JsonElement>("authorization_details", out var details));
        var grant = Assert.Single(details.EnumerateArray());
        Assert.Equal("email", grant.GetProperty("type").GetString());
        Assert.Equal("read", Assert.Single(grant.GetProperty("actions").EnumerateArray()).GetString());
        // The ceiling's constraint rides along even though the request didn't mention it.
        Assert.Equal("@acme.com", Assert.Single(grant.GetProperty("recipient_domains").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Exchange_RequestBeyondCeiling_IsInvalidAuthorizationDetails()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync(AgentClientId);

        var response = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"payments","actions":["refund"]}]""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // RFC 9396 §5 defines invalid_authorization_details for authorization_details the AS will not
        // grant. invalid_target is RFC 8707's code for an unacceptable `resource` — a different
        // parameter — so a client handling the two separately was told to do the wrong thing.
        Assert.Equal("invalid_authorization_details", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exchange_ConsentNarrowerThanCeiling_Wins()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        // The user consents to calendar only — the email ceiling is irrelevant for them.
        await GrantConsentAsync(AgentClientId, AuthoritySet.Of(
            new AuthorityGrant { Type = "calendar", Actions = ["read"] }));

        var denied = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal("invalid_authorization_details",
            (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var allowed = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"calendar","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Exchange_AskPolicy_ParksOnApproval_ThenMintsOnce()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync(AgentClientId);

        // send is ask-gated → the exchange parks and hands back an approval id.
        var pending = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["send"]}]""");
        Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        var pendingBody = await pending.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization_pending", pendingBody.GetProperty("error").GetString());
        var approvalId = pendingBody.GetProperty("approval_id").GetString()!;
        Assert.True(pendingBody.GetProperty("interval").GetInt32() >= 1);

        // The user sees it on their pending list (the login cookie is on _client)...
        var list = await _client.GetFromJsonAsync<JsonElement>("/approvals");
        var item = Assert.Single(list.GetProperty("approvals").EnumerateArray());
        Assert.Equal(approvalId, item.GetProperty("id").GetString());
        Assert.Contains("email:send",
            item.GetProperty("pendingActions").EnumerateArray().Select(a => a.GetString()));

        // ...approves it...
        var resolve = await _client.PostAsJsonAsync($"/approvals/{approvalId}", new { decision = "approve" });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);

        // ...and the agent's retry (same request + approval_id) mints, with ask resolved to auto.
        var minted = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["send"]}]""", approvalId: approvalId);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var token = new JsonWebTokenHandler().ReadJsonWebToken(
            (await minted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!);
        Assert.True(token.TryGetPayloadValue<JsonElement>("authorization_details", out var details));
        var grant = Assert.Single(details.EnumerateArray());
        if (grant.TryGetProperty("action_policies", out var policies))
            Assert.NotEqual("ask", policies.GetProperty("send").GetString());

        // Single-use: the same approval can never mint twice.
        var replay = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["send"]}]""", approvalId: approvalId);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant",
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exchange_DeniedApproval_IsAccessDenied()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync(AgentClientId);

        var pending = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["send"]}]""");
        var approvalId = (await pending.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("approval_id").GetString()!;

        await _client.PostAsJsonAsync($"/approvals/{approvalId}", new { decision = "deny" });

        var denied = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["send"]}]""", approvalId: approvalId);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal("access_denied",
            (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task SubDelegation_RequiresDepthBudget_AndNestsActChain()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync(AgentClientId);
        await GrantConsentAsync(SubAgentClientId);

        var first = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        first.EnsureSuccessStatusCode();
        var delegated = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;

        // Agent 1's profile allows no sub-delegation → the second hop is refused.
        var refused = await ExchangeAsync(SubAgentClientId, SubAgentClientSecret, delegated,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("delegation depth",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_description").GetString());

        // Grant one hop of budget and the same exchange succeeds, chain nested per RFC 8693.
        var profile = (await _factory.AgentProfileStore.GetAsync(AgentClientId))!;
        profile.MaxDelegationDepth = 1;
        await _factory.AgentProfileStore.UpsertAsync(profile);

        var second = await ExchangeAsync(SubAgentClientId, SubAgentClientSecret, delegated,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var token = new JsonWebTokenHandler().ReadJsonWebToken(
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!);
        Assert.True(token.TryGetPayloadValue<JsonElement>("act", out var act));
        Assert.Equal(SubAgentClientId, act.GetProperty("sub").GetString());
        Assert.Equal(AgentClientId, act.GetProperty("act").GetProperty("sub").GetString());
    }

    [Fact]
    public async Task SubDelegation_CanOnlyAttenuate()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync(AgentClientId);
        await GrantConsentAsync(SubAgentClientId);

        var profile = (await _factory.AgentProfileStore.GetAsync(AgentClientId))!;
        profile.MaxDelegationDepth = 1;
        await _factory.AgentProfileStore.UpsertAsync(profile);

        // First hop narrows to calendar only.
        var first = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"calendar","actions":["read"]}]""");
        first.EnsureSuccessStatusCode();
        var delegated = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;

        // The second hop cannot climb back to email — the subject token's own authority binds.
        var refused = await ExchangeAsync(SubAgentClientId, SubAgentClientSecret, delegated,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_authorization_details",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ServiceMode_ClientCredentials_CarriesCeiling_WithAskDegradedToDeny()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.ClientCredentials,
                ["client_id"] = AgentClientId,
                ["client_secret"] = AgentClientSecret,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(body.GetProperty("expires_in").GetInt32(), 1, 300);

        var token = new JsonWebTokenHandler().ReadJsonWebToken(body.GetProperty("access_token").GetString()!);
        Assert.False(token.TryGetPayloadValue<JsonElement>("act", out _)); // no user, no actor chain
        Assert.True(token.TryGetPayloadValue<JsonElement>("authorization_details", out var details));

        var email = details.EnumerateArray().Single(g => g.GetProperty("type").GetString() == "email");
        Assert.Equal("deny", email.GetProperty("action_policies").GetProperty("send").GetString());
    }

    [Fact]
    public async Task ServiceMode_DelegatedOnlyAgent_CannotUseClientCredentials()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.ClientCredentials,
                ["client_id"] = SubAgentClientId,
                ["client_secret"] = SubAgentClientSecret,
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized_client", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConsentEndpoint_GrantsFloor_PreIntersectedWithCeiling()
    {
        var primary = await GetPrimaryAccessTokenAsync();

        // The user tries to consent to more than the ceiling allows (email delete) plus a
        // legitimate slice (email read) — the stored floor must be the intersection.
        var grant = await _client.PostAsJsonAsync("/consent/agents", new
        {
            clientId = AgentClientId,
            authority = JsonDocument.Parse(
                """[{"type":"email","actions":["read","delete"]},{"type":"payments","actions":["refund"]}]""").RootElement,
        });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        var view = await grant.Content.ReadFromJsonAsync<JsonElement>();
        var stored = Assert.Single(view.GetProperty("authority").EnumerateArray());
        Assert.Equal("email", stored.GetProperty("type").GetString());
        Assert.Equal("read", Assert.Single(stored.GetProperty("actions").EnumerateArray()).GetString());

        // The floor works at mint time…
        var allowed = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // …and what the ceiling never allowed stays out.
        var refused = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["delete"]}]""");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // The standing consent is listed for the user.
        var list = await _client.GetFromJsonAsync<JsonElement>("/consent/agents");
        var item = Assert.Single(list.GetProperty("consents").EnumerateArray());
        Assert.Equal(AgentClientId, item.GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task ConsentRevocation_StopsTheNextMint()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync(AgentClientId);

        var first = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var revoke = await _client.DeleteAsync($"/consent/agents/{AgentClientId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var second = await ExchangeAsync(AgentClientId, AgentClientSecret, primary,
            authorizationDetails: """[{"type":"email","actions":["read"]}]""");
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("consent_required",
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_description").GetString());
    }

    // -----------------------------------------------------------------------

    private async Task GrantConsentAsync(string agentClientId, AuthoritySet? floor = null)
    {
        var user = (await _factory.UserStore.FindByEmailAsync("test@example.com"))!;
        await _factory.GrantStore.StoreAsync(new PersistedGrant
        {
            Key = AgentConsent.Key(user.Id, agentClientId),
            Type = AgentConsent.GrantType,
            SubjectId = user.Id,
            ClientId = agentClientId,
            Data = AgentConsent.Serialize(floor ?? Ceiling(), DateTimeOffset.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });
    }

    private Task<HttpResponseMessage> ExchangeAsync(
        string clientId, string clientSecret, string subjectToken,
        string? authorizationDetails = null, string? approvalId = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.TokenExchange,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };
        if (authorizationDetails is not null)
            form["authorization_details"] = authorizationDetails;
        if (approvalId is not null)
            form["approval_id"] = approvalId;
        return _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
    }

    private async Task<string> GetPrimaryAccessTokenAsync()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            "&response_type=code&scope=openid%20profile%20email" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["code_verifier"] = verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
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
