using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The squat, end to end through two real connections: does the genuine user's first login inherit the
/// squatter's ability to sign in, or evict it?
/// </summary>
/// <remarks>
/// <see cref="FederationAdoptionPolicyTests"/> pins the decision; this pins the WIRING, because the decision
/// being right in a static method is not what protects anyone. It also demonstrates that every pre-existing
/// gate passes during the attack, which is why the takeover survived two rounds of fixes:
/// <list type="bullet">
/// <item>the unverified-email gate — the attacker operates the upstream and asserts
/// <c>email_verified: true</c>;</item>
/// <item><c>AllowedDomains</c> — enforced only when non-empty, so the attacker's connection leaves it
/// empty;</item>
/// <item>the domain-routing gate — the squat lands before the victim domain has any row to route.</item>
/// </list>
/// </remarks>
[Collection("Azurite")]
public sealed class FederationSquatEvictionTests : IAsyncLifetime
{
    private const string VictimEmail = "ceo@acme.com";

    private readonly OidcMockHandler _oidcMock = new();
    private readonly AuthagonalTestFactory _factory;
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public FederationSquatEvictionTests(AzuriteFixture azurite)
    {
        _factory = new AuthagonalTestFactory
        {
            OidcHttpHandler = _oidcMock,
            AzuriteConnectionString = azurite.ConnectionString,
        };
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task TheGenuineConnectionEvictsTheSquattersBinding_RatherThanInheritingIt()
    {
        // ── T0: the attacker's connection. AllowedDomains deliberately empty, so nothing restricts the
        // address it may assert — and so Acme's real onboarding is not refused later with domain_claimed.
        var squatterConnection = await CreateConnectionAsync("Attacker IdP", allowedDomains: []);

        // The attacker operates this upstream, so email_verified is theirs to set.
        _oidcMock.Subject = "attacker-1";
        _oidcMock.Email = VictimEmail;
        _oidcMock.EmailVerified = true;

        await FederateAsync(squatterConnection);

        // The squat landed: an account bearing the victim's address, with the attacker able to sign in.
        var squatted = await _factory.UserStore.FindByEmailAsync(VictimEmail);
        Assert.NotNull(squatted);
        var afterSquat = await _factory.UserStore.GetLoginsAsync(squatted!.Id);
        Assert.Contains(afterSquat, l => l.Provider == $"oidc:{squatterConnection}");

        // ── T1: Acme onboards, vouched for its own domain.
        var acmeConnection = await CreateConnectionAsync("Acme IdP", allowedDomains: ["acme.com"]);

        // ── T2: the genuine user's first login. Different subject, same address.
        _oidcMock.Subject = "real-ceo";
        _oidcMock.Email = VictimEmail;
        _oidcMock.EmailVerified = true;

        await FederateAsync(acmeConnection);

        // Adoption happened — the genuine user is on the account, not locked out of it.
        var adopted = await _factory.UserStore.FindByEmailAsync(VictimEmail);
        Assert.NotNull(adopted);
        Assert.Equal(squatted.Id, adopted!.Id);

        var afterAdoption = await _factory.UserStore.GetLoginsAsync(adopted.Id);
        Assert.Contains(afterAdoption, l => l.Provider == $"oidc:{acmeConnection}");

        // The whole point: the squatter can no longer sign in to it.
        Assert.DoesNotContain(afterAdoption, l => l.Provider == $"oidc:{squatterConnection}");
        Assert.Null(await _factory.UserStore.FindLoginAsync($"oidc:{squatterConnection}", "attacker-1"));
    }

    /// <summary>
    /// The control: an ordinary federated login through one connection keeps its own binding.
    /// </summary>
    /// <remarks>
    /// Without this, an eviction that removed every binding — including the connection's own — would satisfy
    /// the assertions above while breaking every returning federated user.
    /// </remarks>
    [Fact]
    public async Task AnOrdinaryFederatedLogin_KeepsItsOwnBinding()
    {
        var connection = await CreateConnectionAsync("Acme IdP", allowedDomains: ["acme.com"]);

        _oidcMock.Subject = "real-ceo";
        _oidcMock.Email = VictimEmail;
        _oidcMock.EmailVerified = true;

        await FederateAsync(connection);
        await FederateAsync(connection);   // returning user

        var user = await _factory.UserStore.FindByEmailAsync(VictimEmail);
        var logins = await _factory.UserStore.GetLoginsAsync(user!.Id);

        Assert.Contains(logins, l => l.Provider == $"oidc:{connection}");
        Assert.NotNull(await _factory.UserStore.FindLoginAsync($"oidc:{connection}", "real-ceo"));
    }

    private async Task FederateAsync(string connectionId)
    {
        var login = await _client.GetAsync($"/oidc/{connectionId}/login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var qs = HttpUtility.ParseQueryString(new Uri(login.Headers.Location!.ToString()).Query);
        _oidcMock.Nonce = qs["nonce"]!;

        var callback = await _client.GetAsync(
            $"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(qs["state"]!)}");

        Assert.True(callback.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK,
            $"federation through {connectionId} failed: {callback.StatusCode} " +
            $"{callback.Headers.Location}");
    }

    private async Task<string> CreateConnectionAsync(string name, string[] allowedDomains)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/oidc/connections")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                connectionName = name,
                metadataLocation = $"{_oidcMock.Issuer}/.well-known/openid-configuration",
                clientId = "test-oidc-client",
                clientSecret = "test-oidc-secret",
                redirectUrl = $"{AuthagonalTestFactory.TestIssuer}/oidc/callback",
                allowedDomains,
                jitProvisioningEnabled = true,
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionId").GetString()!;
    }
}
