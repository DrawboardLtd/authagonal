using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Authority;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// The residuals of the 2026-07 review: findings reported fixed whose fix landed on one host, one
/// provider or one call site and missed its twin. Each test here names the sibling that was missed.
/// </summary>
public sealed class OAuthResidualHardeningTests : IAsyncLifetime
{
    // DCR is off by default, and three of the tests below exercise it.
    private readonly AuthagonalTestFactory _factory = new()
    {
        ConfigureAuthOptions = o => o.DynamicClientRegistrationEnabled = true,
    };
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // =====================================================================================
    // #54 — the admin-scope reservation, on the two guards that still compared whole elements
    // =====================================================================================

    /// <summary>
    /// A stored AllowedScopes entry is not necessarily one scope. The admin API and DCR both split on
    /// whitespace before comparing; the impersonation endpoint did not, and it DEFAULTS its scope list
    /// to client.AllowedScopes verbatim — so a client row carrying "openid authagonal-admin" (seeded,
    /// imported, or written before the API guard existed) minted an admin token from this endpoint.
    /// </summary>
    [Fact]
    public async Task AdminMint_ClientScopeWithEmbeddedAdminScope_IsRefused()
    {
        var adminToken = await _factory.GetAdminTokenAsync(_client);
        var user = await _factory.SeedTestUserAsync(email: "victim@example.com");

        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "backdoor-client",
            ClientName = "Backdoor",
            RequireClientSecret = false,
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid authagonal-admin"],
        });

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/token?clientId=backdoor-client&userId={Uri.EscapeDataString(user.Id)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden_scope", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// The universal backstop: no code path may emit a `scope` claim that parses back into more scopes
    /// than it was handed, whatever ingress wrote the client row. Uses a NON-admin joint entry so it is
    /// the mint guard being exercised and not the reservation above.
    /// </summary>
    [Fact]
    public async Task AdminMint_ClientScopeContainingWhitespace_MintsNoToken()
    {
        var adminToken = await _factory.GetAdminTokenAsync(_client);
        var user = await _factory.SeedTestUserAsync(email: "victim2@example.com");

        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "joint-scope-client",
            ClientName = "Joint",
            RequireClientSecret = false,
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid profile"],
        });

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/token?clientId=joint-scope-client&userId={Uri.EscapeDataString(user.Id)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var response = await _client.SendAsync(request);

        // Fail closed. A 500 from the mint is the correct outcome for a row no ingress should have
        // been able to write — what must not happen is a signed token whose scope claim splits into
        // two scopes the guards only ever saw as one.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("access_token", await response.Content.ReadAsStringAsync());
    }

    /// <summary>And so does the scope registry, which is where such a name would have to come from.</summary>
    [Fact]
    public async Task AdminScopeApi_NameWithWhitespace_IsRefused()
    {
        var adminToken = await _factory.GetAdminTokenAsync(_client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scopes/")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { name = "openid authagonal-admin", displayName = "Sneaky" }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================================
    // #124 — /connect/deviceauthorization was the client-authenticated endpoint with no throttle
    // =====================================================================================

    [Fact]
    public async Task DeviceAuthorization_RepeatedBadClientSecret_IsThrottled()
    {
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId);
        client!.AllowedGrantTypes = ["client_credentials", GrantTypes.DeviceCode];
        await _factory.ClientStore.UpsertAsync(client);

        static FormUrlEncodedContent Attempt() => new(new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = "wrong-secret",
            ["scope"] = "openid",
        });

        // The budget is 30/min on `client-secret|{clientId}` — the same key ClientAuthentication uses,
        // so an attacker cannot buy a fresh allowance by moving between endpoints.
        string? description = null;
        for (var i = 0; i < 31; i++)
        {
            var response = await _client.PostAsync("/connect/deviceauthorization", Attempt());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            description = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error_description").GetString();
        }

        Assert.Equal("Too many authentication attempts", description);
    }

    // =====================================================================================
    // #121 — anonymous registration could still assert the federation trust marker about itself
    // =====================================================================================

    [Fact]
    public async Task Register_CannotSetReservedCustomAttributes()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "selfserve@example.com",
            password = "Test1234!",
            customAttributes = new Dictionary<string, string>
            {
                ["federated_connection"] = "acme-entra",
                ["org_id"] = "org-of-someone-else",
                ["roles"] = "admin",
                ["department"] = "engineering",
            },
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var user = await _factory.UserStore.FindByEmailAsync("selfserve@example.com");
        Assert.NotNull(user);
        // The trust markers are refused...
        Assert.False(user!.CustomAttributes.ContainsKey("federated_connection"));
        Assert.False(user.CustomAttributes.ContainsKey("org_id"));
        Assert.False(user.CustomAttributes.ContainsKey("roles"));
        // ...and an ordinary attribute still lands, so the filter is a denylist and not an outage.
        Assert.Equal("engineering", user.CustomAttributes["department"]);
    }

    // =====================================================================================
    // #64 — DCR accepted any token_endpoint_auth_method and answered private_key_jwt with a secret
    // =====================================================================================

    [Fact]
    public async Task Discovery_AdvertisesTheAssertionAlgorithmsItAccepts()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        var algs = doc.GetProperty("token_endpoint_auth_signing_alg_values_supported")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("RS256", algs);
        Assert.Contains("ES256", algs);
        // Asymmetric only — a symmetric entry here would advertise a way to turn client authentication
        // into an HMAC over a key the client publishes.
        Assert.DoesNotContain("HS256", algs);
    }

    [Fact]
    public async Task DynamicRegistration_UnknownAuthMethod_IsRefused()
    {
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Typo",
            redirect_uris = new[] { "https://rp.example.com/cb" },
            token_endpoint_auth_method = "client_secret_jwt",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_client_metadata",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task DynamicRegistration_PrivateKeyJwtWithoutJwks_IsRefused()
    {
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Keyless",
            redirect_uris = new[] { "https://rp.example.com/cb" },
            token_endpoint_auth_method = "private_key_jwt",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DynamicRegistration_PrivateKeyJwtWithJwks_BindsTheKeyAndIssuesNoSecret()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(false);
        var jwks = new
        {
            keys = new[]
            {
                new
                {
                    kty = "EC",
                    crv = "P-256",
                    kid = "reg-key-1",
                    x = Base64UrlEncode(parameters.Q.X!),
                    y = Base64UrlEncode(parameters.Q.Y!),
                },
            },
        };

        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Assertion Client",
            redirect_uris = new[] { "https://rp.example.com/cb" },
            token_endpoint_auth_method = "private_key_jwt",
            jwks,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // A client whose whole point is not to hold a bearer secret must not be handed one.
        Assert.False(body.TryGetProperty("client_secret", out _));
        Assert.Equal("private_key_jwt", body.GetProperty("token_endpoint_auth_method").GetString());

        var registered = await _factory.ClientStore.GetAsync(body.GetProperty("client_id").GetString()!);
        Assert.NotNull(registered);
        Assert.Contains("reg-key-1", registered!.JwksJson);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // =====================================================================================
    // #190 — the device flow had no server-side "no", and recorded no consent
    // =====================================================================================

    [Fact]
    public async Task DeviceFlow_Deny_MakesTheTokenEndpointSayAccessDenied()
    {
        var codes = await StartDeviceFlowAsync();
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var deny = await _client.PostAsync("/api/auth/device/deny", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["user_code"] = codes.UserCode }));
        Assert.Equal(HttpStatusCode.OK, deny.StatusCode);

        var poll = await _client.PostAsync("/connect/token", DevicePollForm(codes.DeviceCode));
        Assert.Equal(HttpStatusCode.BadRequest, poll.StatusCode);
        Assert.Equal("access_denied",
            (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeviceFlow_Approve_RecordsAConsentGrantNarrowedToWhatWasApproved()
    {
        var codes = await StartDeviceFlowAsync(scope: "openid profile email");
        var user = await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // The user deselects `email` on the approval screen — the device asked for three scopes and is
        // granted two. Before this the choice did not exist: approval was all-or-nothing.
        var approve = await _client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["user_code"] = codes.UserCode,
                ["scopes"] = "openid profile",
            }));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var consent = await _factory.GrantStore.GetAsync($"consent:{user.Id}:{AuthagonalTestFactory.AdminClientId}");
        Assert.NotNull(consent);
        var consentData = JsonSerializer.Deserialize<JsonElement>(consent!.Data);
        var granted = consentData.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("openid", granted);
        Assert.Contains("profile", granted);
        Assert.DoesNotContain("email", granted);

        // And the token the device is issued carries only the narrowed set.
        var poll = await _client.PostAsync("/connect/token", DevicePollForm(codes.DeviceCode));
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var accessToken = (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        var scopeClaim = new JsonWebTokenHandler().ReadJsonWebToken(accessToken).GetPayloadValue<string>("scope");
        Assert.DoesNotContain("email", scopeClaim.Split(' '));
    }

    private static FormUrlEncodedContent DevicePollForm(string deviceCode) => new(new Dictionary<string, string>
    {
        ["grant_type"] = GrantTypes.DeviceCode,
        ["device_code"] = deviceCode,
        ["client_id"] = AuthagonalTestFactory.AdminClientId,
        ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
    });

    private async Task<(string DeviceCode, string UserCode)> StartDeviceFlowAsync(string scope = "openid profile")
    {
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId);
        client!.AllowedGrantTypes = ["client_credentials", GrantTypes.DeviceCode];
        client.AllowedScopes = ["openid", "profile", "email", "offline_access", AuthagonalTestFactory.AdminScope];
        await _factory.ClientStore.UpsertAsync(client);

        var response = await _client.PostAsync("/connect/deviceauthorization", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
                ["scope"] = scope,
            }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("device_code").GetString()!, body.GetProperty("user_code").GetString()!);
    }
}

/// <summary>
/// #119 — the client-secret throttle in ClientAuthentication is `limiter is not null`-gated, and
/// AddAuthagonalProtocol registered no IRateLimiter. For an embedder that takes the protocol package
/// without Authagonal.Server, the guard was dead code and /connect/token was an unbounded
/// secret-guessing and KDF-CPU oracle.
/// </summary>
public sealed class ProtocolHostClientSecretThrottleTests : IAsyncDisposable
{
    private readonly ProtocolTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task ProtocolOnlyHost_RepeatedBadClientSecret_IsThrottled()
    {
        var client = _host.CreateClient();

        static FormUrlEncodedContent Attempt() => new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ProtocolTestHost.MachineClientId,
            ["client_secret"] = "wrong-secret",
            ["scope"] = "machine-api",
        });

        string? description = null;
        for (var i = 0; i < 31; i++)
        {
            var response = await client.PostAsync("/connect/token", Attempt());
            description = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error_description").GetString();
        }

        Assert.Equal("Too many authentication attempts", description);
    }
}

/// <summary>
/// #54, seeder half — AddAuthagonal registers ProtocolSeedService inside every Server host, so a host
/// that binds AuthagonalProtocolOptions from configuration had a second route into IClientStore that
/// applied none of the checks the Server's own seeder does.
/// </summary>
/// <remarks>
/// This class asserted the ADMIN LOCKOUT as intended behaviour. Both seeders refused a descriptor carrying
/// the administrative scope, and this test pinned it — while <c>docs/admin-api.md</c> names a config-seeded
/// <c>client_credentials</c> client holding exactly that scope as the only way to mint the first admin token.
/// So a fresh deployment could never reach <c>/api/v1/*</c>, and because the seeders log at Error and SKIP,
/// rotating a compromised admin secret wrote nothing and the old credential kept working.
/// <para>
/// The reservation is real and stays on every path a caller can reach. Configuration is not a caller — see
/// <c>ClientSeedPolicy.Reject</c>. The two halves are now asserted together, in one test, because they were
/// only ever separable on paper: what makes the malformed entry a defect is that it smuggles the scope past a
/// whole-string comparison, and what makes the plain entry legitimate is that config already holds the keys.
/// </para>
/// </remarks>
public sealed class ProtocolSeedReservationTests
{
    [Fact]
    public async Task ProtocolSeeder_SeedsTheAdminScope_AndStillRefusesAMalformedEntry()
    {
        var clientStore = new InMemoryClientStore();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IClientStore>(clientStore);
        builder.Services.AddSingleton<IScopeStore>(new InMemoryScopeStore());
        builder.Services.AddSingleton<IGrantStore>(new InMemoryGrantStore());
        builder.Services.AddSingleton<ISigningKeyStore>(new InMemorySigningKeyStore());
        builder.Services.AddSingleton<ITenantContext>(new TestTenantContext("https://seed.test.local"));
        builder.Services.AddAuthagonalProtocol(o =>
        {
            o.Clients =
            [
                // The documented bootstrap client, as docs/admin-api.md writes it.
                new OidcClientDescriptor
                {
                    ClientId = "admin-cli",
                    AllowedScopes = ["openid", AdminScopeReservation.DefaultAdminScope],
                },
                new OidcClientDescriptor
                {
                    ClientId = "joint-scope-client",
                    AllowedScopes = ["openid authagonal-admin"],
                },
                new OidcClientDescriptor
                {
                    ClientId = "wellformed-client",
                    AllowedScopes = ["openid", "profile"],
                },
            ];
        });

        var app = builder.Build();
        await app.StartAsync();
        try
        {
            // The admin API is reachable on a fresh install: the seeded client holds the scope, and
            // /connect/token issues it against AllowedScopes. Refusing this is what locked deployments out.
            var admin = await clientStore.GetAsync("admin-cli");
            Assert.NotNull(admin);
            Assert.Contains(AdminScopeReservation.DefaultAdminScope, admin!.AllowedScopes);

            // Still refused: one stored entry, two scopes on the wire. A client that does not say what it
            // appears to say is a misconfiguration whichever scope it smuggles.
            Assert.Null(await clientStore.GetAsync("joint-scope-client"));

            // A well-formed entry still seeds — the guard refuses the misconfiguration, not the feature.
            Assert.NotNull(await clientStore.GetAsync("wellformed-client"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

/// <summary>
/// #37 — the org_id half was closed by reserving four claim names; the stored-CustomAttributes half
/// was not. Any claim a scope releases that is NOT one of those four was still taken from the upstream
/// id_token in preference to this server's own user record.
/// </summary>
public sealed class FederationClaimPrecedenceTests
{
    [Fact]
    public async Task StoredAttributeBeatsTheUpstreamsValue_AndAGapIsStillFilled()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        // A scope that releases two custom claims. The user record holds one of them.
        await factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "hr",
            DisplayName = "HR",
            UserClaims = ["department", "upstream_only"],
        });
        var client = (await factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!;
        client.AllowedScopes = [.. client.AllowedScopes, "hr"];
        await factory.ClientStore.UpsertAsync(client);

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<Authagonal.Protocol.Services.IProtocolTokenService>();

        var subject = new OidcSubject
        {
            SubjectId = "user-1",
            Email = "user@example.com",
            CustomAttributes = new Dictionary<string, string> { ["department"] = "engineering" },
            // What a customer-controlled IdP asserted about their own user on the last hop.
            FederationClaims = new Dictionary<string, string>
            {
                ["department"] = "finance",
                ["upstream_only"] = "from-upstream",
            },
        };

        var accessToken = await tokens.CreateAccessTokenAsync(subject, client, ["openid", "hr"]);
        var payload = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);

        // The store is authoritative where it holds a value...
        Assert.Equal("engineering", payload.GetPayloadValue<string>("department"));
        // ...and the upstream still fills a gap the store has nothing for, so federation is narrowed,
        // not switched off.
        Assert.Equal("from-upstream", payload.GetPayloadValue<string>("upstream_only"));
    }
}

/// <summary>
/// #36 (member half) and #92 (approval poll) — both on the agentic delegation path.
/// </summary>
public sealed class AgentAuthorityResidualTests : IAsyncLifetime
{
    private const string AgentClientId = "residual-agent";
    private const string AgentClientSecret = "residual-agent-secret";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = AgentClientId,
            ClientName = "Residual Agent",
            RequireClientSecret = true,
            ClientSecretHashes = [hasher.HashPassword(AgentClientSecret)],
            AllowedGrantTypes = [GrantTypes.TokenExchange, GrantTypes.ClientCredentials],
            AllowedScopes = ["openid", "profile", "email"],
            AccessTokenLifetimeSeconds = 3600,
        });

        await _factory.AgentProfileStore.UpsertAsync(new AgentProfile
        {
            ClientId = AgentClientId,
            Mode = AgentMode.Both,
            Ceiling = Ceiling(),
            MaxDelegationDepth = 0,
            MaxTokenLifetimeSeconds = 300,
        });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // The ceiling names ONE constraint. Anything else a request calls a constraint was granted by
    // nobody — payments:refund is ask-gated so the approval path can be exercised too.
    private static AuthoritySet Ceiling() => AuthoritySet.Of(
        new AuthorityGrant
        {
            Type = "payments",
            Actions = ["initiate", "refund"],
            ActionPolicies = new Dictionary<string, ActionPolicy> { ["refund"] = ActionPolicy.Ask },
            Constraints = new Dictionary<string, ConstraintValue>
            {
                ["max_amount"] = ConstraintValue.Of(100m),
            },
        });

    // -------------------------------------------------------------------------------------
    // #36 — an unrecognised MEMBER became a "constraint" and rode the intersection into the claim
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Algebra_RequestOnlyConstraintIsReported_GrantedOneIsNot()
    {
        var granted = AuthoritySet.Of(new AuthorityGrant
        {
            Type = "payments",
            Actions = ["initiate"],
            Constraints = new Dictionary<string, ConstraintValue> { ["max_amount"] = ConstraintValue.Of(100m) },
        });

        var narrowing = AuthoritySet.Of(new AuthorityGrant
        {
            Type = "payments",
            Actions = ["initiate"],
            Constraints = new Dictionary<string, ConstraintValue> { ["max_amount"] = ConstraintValue.Of(20m) },
        });
        Assert.Null(granted.FindUngrantedConstraint(narrowing));

        var invented = AuthoritySet.Of(new AuthorityGrant
        {
            Type = "payments",
            Actions = ["initiate"],
            Constraints = new Dictionary<string, ConstraintValue> { ["beneficiary"] = ConstraintValue.Of("attacker") },
        });
        var found = granted.FindUngrantedConstraint(invented);
        Assert.NotNull(found);
        Assert.Equal(("payments", "beneficiary"), (found!.Value.Type, found.Value.Member));
    }

    [Fact]
    public async Task Exchange_RequestInventsAMember_IsRefused()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync();

        // `beneficiary` is not a member of anything the ceiling or the consent defines. It used to be
        // parsed as a constraint, carried through the one-sided meet, and SIGNED into the
        // authorization_details claim — where a resource server cannot tell a restriction the user
        // imposed from authority this server conferred.
        var response = await ExchangeAsync(primary,
            """[{"type":"payments","actions":["initiate"],"beneficiary":"attacker-iban"}]""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_authorization_details", body.GetProperty("error").GetString());
        Assert.Contains("beneficiary", body.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Exchange_RequestNarrowsAGrantedMember_StillSucceeds()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync();

        var response = await ExchangeAsync(primary,
            """[{"type":"payments","actions":["initiate"],"max_amount":20}]""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AgentConsent_CannotExtendTheCeilingsVocabulary()
    {
        var user = await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var response = await _client.PostAsJsonAsync("/consent/agents", new
        {
            clientId = AgentClientId,
            authority = JsonSerializer.Deserialize<JsonElement>(
                """[{"type":"payments","actions":["initiate"],"beneficiary":"attacker-iban"}]"""),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_authorization_details",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(await _factory.GrantStore.GetAsync(AgentConsent.Key(user.Id, AgentClientId)));
    }

    // -------------------------------------------------------------------------------------
    // #92 — a poll used to write the whole payload back and could revert the user's decision
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A pending poll must leave the approval row byte-identical.
    /// </summary>
    /// <remarks>
    /// The revert was structural, not a wide window: the poll persisted LastPolledAt by re-serialising
    /// the WHOLE payload it had read, so whatever else that payload said travelled with it. Whether the
    /// user's decision survives then depends entirely on scheduling. The only property that closes it is
    /// that a poll writes nothing at all — which is exactly what this asserts, and what fails the moment
    /// a poll marker is persisted again.
    /// </remarks>
    [Fact]
    public async Task ApprovalPoll_WritesNothingBack()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync();

        var details = """[{"type":"payments","actions":["refund"]}]""";
        var parked = await ExchangeAsync(primary, details);
        var approvalId = (await parked.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("approval_id").GetString()!;

        var key = Approval.Key(approvalId);
        var before = (await _factory.GrantStore.GetAsync(key))!.Data;

        var poll = await ExchangeAsync(primary, details, approvalId: approvalId);
        Assert.Equal("authorization_pending",
            (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Equal(before, (await _factory.GrantStore.GetAsync(key))!.Data);
    }

    /// <summary>The user's answer stands, and a later poll reports it rather than overwriting it.</summary>
    [Fact]
    public async Task ApprovalPoll_AfterDeny_ReportsTheDenialAndLeavesItStanding()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync();

        var details = """[{"type":"payments","actions":["refund"]}]""";
        var parked = await ExchangeAsync(primary, details);
        var approvalId = (await parked.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("approval_id").GetString()!;

        var user = (await _factory.UserStore.FindByEmailAsync("test@example.com"))!;
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync($"/approvals/{approvalId}", new { decision = "deny" })).StatusCode);

        var poll = await ExchangeAsync(primary, details, approvalId: approvalId);
        Assert.Equal("access_denied",
            (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var stored = Approval.Parse((await _factory.GrantStore.GetAsync(Approval.Key(approvalId)))!.Data);
        Assert.Equal(ApprovalStatus.Denied, stored!.Status);
        Assert.Equal(user.Id, stored.ResolvedBy);
    }

    [Fact]
    public async Task ApprovalPoll_ConcurrentPolls_ShareOneThrottleBudget()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        await GrantConsentAsync();

        var details = """[{"type":"payments","actions":["refund"]}]""";
        var first = await ExchangeAsync(primary, details);
        var approvalId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("approval_id").GetString()!;

        // Two polls issued together. The old throttle was read-check-write on LastPolledAt, so both
        // read the old timestamp, both passed, and both wrote — the interval bound defeated by exactly
        // the parallelism it exists to bound. The limiter's check-and-increment is atomic, so one of
        // these must be told to slow down.
        var polls = await Task.WhenAll(
            ExchangeAsync(primary, details, approvalId: approvalId),
            ExchangeAsync(primary, details, approvalId: approvalId));

        var errors = new List<string>();
        foreach (var poll in polls)
            errors.Add((await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!);

        Assert.Contains("slow_down", errors);
    }

    // -------------------------------------------------------------------------------------

    private async Task GrantConsentAsync()
    {
        var user = (await _factory.UserStore.FindByEmailAsync("test@example.com"))!;
        await _factory.GrantStore.StoreAsync(new PersistedGrant
        {
            Key = AgentConsent.Key(user.Id, AgentClientId),
            Type = AgentConsent.GrantType,
            SubjectId = user.Id,
            ClientId = AgentClientId,
            Data = AgentConsent.Serialize(Ceiling(), DateTimeOffset.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });
    }

    private Task<HttpResponseMessage> ExchangeAsync(
        string subjectToken, string authorizationDetails, string? approvalId = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.TokenExchange,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["client_id"] = AgentClientId,
            ["client_secret"] = AgentClientSecret,
            ["authorization_details"] = authorizationDetails,
        };
        if (approvalId is not null)
            form["approval_id"] = approvalId;
        return _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
    }

    private async Task<string> GetPrimaryAccessTokenAsync()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            "&response_type=code&scope=openid%20profile%20email" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var tokenResponse = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["client_id"] = AuthagonalTestFactory.TestClientId,
                ["code_verifier"] = verifier,
            }));
        tokenResponse.EnsureSuccessStatusCode();
        return (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}

/// <summary>
/// #54, ingress half — the admin client API compares whole AllowedScopes ELEMENTS, both for the
/// reserved scope and through IClientScopeGuard, so a single entry containing whitespace slipped past
/// both and expanded into several scopes on the wire. Uses the bespoke AdminSurfaceHost because
/// AuthagonalTestFactory registers no IClientScopeGuard / IAuditLogger and these routes 400 on binding
/// without them.
/// </summary>
public sealed class AdminClientScopeSyntaxTests : IAsyncLifetime
{
    private readonly AdminSurfaceHost _host = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _host.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task CreateClient_ScopeEntryWithWhitespace_IsRefused()
    {
        var response = await _client.PostAsync("/api/v1/clients/", Json(new
        {
            clientId = "joint-scope-create",
            clientName = "Joint",
            allowedScopes = new[] { "openid profile" },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // On the reason, not just the status — this endpoint has several other 400s and the point is
        // that the joint scope entry is the one that stopped it.
        Assert.Contains("not a single scope token",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_description").GetString());
        Assert.Null(await _host.ClientStore.GetAsync("joint-scope-create"));
    }

    [Fact]
    public async Task UpdateClient_ScopeEntryWithWhitespace_IsRefused()
    {
        Assert.Equal(HttpStatusCode.Created,
            (await _client.PostAsync("/api/v1/clients/", Json(new
            {
                clientId = "joint-scope-update",
                clientName = "Joint",
                allowedScopes = new[] { "openid" },
            }))).StatusCode);

        var response = await _client.PutAsync("/api/v1/clients/joint-scope-update", Json(new
        {
            clientId = "joint-scope-update",
            clientName = "Joint",
            allowedScopes = new[] { "openid profile" },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["openid"], (await _host.ClientStore.GetAsync("joint-scope-update"))!.AllowedScopes);
    }
}
