using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// A partial update preserves what it does not mention, and a public client id is not a shared fuse.
/// </summary>
public sealed class SeedAndBudgetPreservationTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        // A declared proxy, so the per-source budget dimension is the forwarded client address. With nothing
        // declared, TestServer reports no peer and every request collapses into one bucket — the documented
        // undeclared-proxy behaviour, separately warned about at startup.
        _factory.Configuration["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1";
        _factory.Configuration["ForwardedHeaders:KnownNetworks:0"] = "0.0.0.0/0";

        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>
    /// A PUT that omits <c>mode</c> keeps the stored one.
    /// </summary>
    /// <remarks>
    /// <c>UpsertAgent</c> is explicitly a partial update — <c>MaxDelegationDepth</c>,
    /// <c>MaxTokenLifetimeSeconds</c>, <c>HighRiskDefault</c> and <c>Ceiling</c> all fall back to the stored
    /// profile, and the comment added when <c>Ceiling</c> had this same asymmetry claims "as every other field
    /// on this endpoint does". <c>Mode</c> was the field it was wrong about:
    /// <c>AgentModes.Parse(null)</c> returns <c>Delegated</c>, and that was written with no fallback. So a PUT
    /// changing only a lifetime silently reset which delegation machinery the agent may use.
    /// </remarks>
    [Fact]
    public async Task AnOmittedAgentModeIsPreserved()
    {
        var clientId = await SeedAgentClientAsync();

        // Registered as `both`, which needs both grant types — the client above allows them.
        var created = await PutAgentAsync(clientId, new { mode = "both", maxTokenLifetimeSeconds = 600 });
        Assert.True(created.IsSuccessStatusCode, $"create failed: {created.StatusCode}");
        Assert.Equal("both", await ReadModeAsync(clientId));

        // A partial update that says nothing about mode.
        var updated = await PutAgentAsync(clientId, new { maxTokenLifetimeSeconds = 900 });
        Assert.True(updated.IsSuccessStatusCode, $"update failed: {updated.StatusCode}");

        Assert.Equal("both", await ReadModeAsync(clientId));
    }

    /// <summary>An unrecognised mode is refused rather than parsed into a real one.</summary>
    /// <remarks>
    /// <c>Parse</c> maps anything it does not recognise to <c>Delegated</c>, and it used to run BEFORE the
    /// allow-list check. Order matters if the parse ever becomes load-bearing.
    /// </remarks>
    [Fact]
    public async Task AnUnrecognisedAgentModeIsRefused()
    {
        var clientId = await SeedAgentClientAsync();

        var response = await PutAgentAsync(clientId, new { mode = "superuser" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// One source cannot spend the whole fleet's device-authorization budget.
    /// </summary>
    /// <remarks>
    /// The key was <c>device-auth|{clientId}</c> with no source component, and this endpoint is anonymous by
    /// design — RFC 8628 exists for devices that cannot hold a secret, and a public client authenticates on a
    /// bare <c>client_id</c> readable from any SPA's traffic or any shipped firmware. So it was one shared
    /// bucket per publicly-known id, and the only party inconvenienced by exhausting it was the legitimate
    /// fleet: 120 requests a minute from anywhere disabled device login for every device of that client.
    /// </remarks>
    [Fact]
    public async Task OneSourceCannotDisableDeviceLoginForTheFleet()
    {
        await EnableDeviceGrantAsync();

        // Well past the per-source budget.
        for (var i = 0; i < 40; i++)
            await DeviceAuthorizeAsync(from: "203.0.113.9");

        var attacker = await DeviceAuthorizeAsync(from: "203.0.113.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, attacker.StatusCode);

        // A real device, from anywhere else, still gets a code.
        var device = await DeviceAuthorizeAsync(from: "198.51.100.7");
        Assert.Equal(HttpStatusCode.OK, device.StatusCode);
    }

    /// <summary>
    /// Re-seeding a SAML connection preserves the SP keypair and the admin's signing decision.
    /// </summary>
    /// <remarks>
    /// <c>SeedSamlProviders</c> built a brand-new <c>SamlProviderConfig</c> from the seed section and upserted
    /// it, and <c>SamlProviderSeed</c> has no field for <c>SpCertificate</c>, <c>SignAuthnRequests</c>,
    /// <c>NameIdFormat</c>, <c>MetadataXml</c> or <c>IconUrl</c>. So on every pod start, for any connection
    /// named in config, all of those were written back as NULL — destroying the SP keypair that
    /// EncryptedAssertion decryption, signed AuthnRequests and signed logout all resolve by name, and
    /// reverting admin-set request signing to unsigned. <c>CreatedAt</c> was reset to now as well.
    /// <para>
    /// Same defect class the two client seeders already record as fixed: "every property the seed does not
    /// state reverted to the MODEL DEFAULT on each pod start — silently undoing admin hardening applied
    /// through PUT". This was the third seeder and it had not been converted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReSeedingASamlConnectionPreservesTheSpKeypairAndSigningDecision()
    {
        const string connectionId = "seeded-conn";
        var createdAt = DateTimeOffset.UtcNow.AddDays(-30);

        // A connection as it exists after an admin created it and hardened it.
        await _factory.SamlProviderStore.UpsertAsync(new SamlProviderConfig
        {
            ConnectionId = connectionId,
            ConnectionName = "Acme",
            EntityId = "https://sp.test/acme",
            MetadataLocation = "https://idp.test/metadata",
            SpCertificate = "vault://saml-seeded-conn-sp-key",
            SignAuthnRequests = true,
            NameIdFormat = "none",
            IconUrl = "https://cdn.test/acme.png",
            AllowedDomains = ["acme.test"],
            CreatedAt = createdAt,
        });

        // A boot whose config names that connection.
        var seeder = new Authagonal.Server.Services.ProviderSeedService(
            _factory.SamlProviderStore,
            _factory.OidcProviderStore,
            _factory.SsoDomainStore,
            _factory.Services.GetRequiredService<Authagonal.Core.Services.ISecretProvider>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SamlProviders:0:ConnectionId"] = connectionId,
                    ["SamlProviders:0:EntityId"] = "https://sp.test/acme",
                    ["SamlProviders:0:MetadataLocation"] = "https://idp.test/metadata",
                    ["SamlProviders:0:AllowedDomains:0"] = "acme.test",
                })
                .Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Authagonal.Server.Services.ProviderSeedService>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        var after = await _factory.SamlProviderStore.GetAsync(connectionId);
        Assert.NotNull(after);
        Assert.Equal("vault://saml-seeded-conn-sp-key", after.SpCertificate);
        Assert.True(after.SignAuthnRequests);
        Assert.Equal("none", after.NameIdFormat);
        Assert.Equal("https://cdn.test/acme.png", after.IconUrl);
        Assert.Equal(createdAt.ToUnixTimeSeconds(), after.CreatedAt.ToUnixTimeSeconds());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> DeviceAuthorizeAsync(string from)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/deviceauthorization")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = AuthagonalTestFactory.TestClientId,
                ["scope"] = "openid",
            }),
        };
        request.Headers.Add("X-Forwarded-For", from);
        return await _client.SendAsync(request);
    }

    private async Task EnableDeviceGrantAsync()
    {
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AllowedGrantTypes = [.. client.AllowedGrantTypes, Authagonal.Core.Constants.GrantTypes.DeviceCode];
        await _factory.ClientStore.UpsertAsync(client);
    }

    private async Task<string> SeedAgentClientAsync()
    {
        var clientId = $"agent-{Guid.NewGuid():N}";
        var hasher = _factory.Services.GetRequiredService<Authagonal.Server.Services.PasswordHasher>();
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = clientId,
            ClientName = "Agent",
            RequireClientSecret = true,
            ClientSecretHashes = [hasher.HashPassword("secret-of-sufficient-length-00000")],
            AllowedGrantTypes =
            [
                Authagonal.Core.Constants.GrantTypes.TokenExchange,
                Authagonal.Core.Constants.GrantTypes.ClientCredentials,
            ],
            AllowedScopes = ["openid"],
            AccessTokenLifetimeSeconds = 3600,
        });
        return clientId;
    }

    private async Task<HttpResponseMessage> PutAgentAsync(string clientId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agents/{clientId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        return await _client.SendAsync(request);
    }

    private async Task<string?> ReadModeAsync(string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agents/{clientId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("mode").GetString();
    }
}
