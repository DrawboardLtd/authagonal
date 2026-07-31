using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// F281 / F67 — two places where an advertised or recorded value disagreed with what actually
/// happened.
/// </summary>
public sealed class RegistrationAndConsentBindingTests : IAsyncLifetime
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
    // F281 — the offered consent set comes from the server, not the caller
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConsentPost_CannotWidenTheOfferedSetThroughReturnUrl()
    {
        var user = await _factory.SeedTestUserAsync();
        await ConsentClientAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // No authorize request happened, so nothing recorded an offer. A returnUrl claiming a wide
        // scope used to become the recorded "already asked about" set — and AuthorizeEndpoint
        // suppresses the consent prompt for anything inside it, so this was a way to never be asked
        // about `email` or `profile` again.
        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "allow",
            scopes = new[] { "openid" },
            returnUrl = "/connect/authorize?client_id=" + ConsentClientId + "&scope=openid%20profile%20email",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReadConsentAsync(user.Id);
        Assert.NotNull(stored);
        Assert.Equal(["openid"], stored!.OfferedScopes ?? []);
    }

    [Fact]
    public async Task ConsentPost_RecordsWhatTheAuthorizeEndpointOffered()
    {
        var user = await _factory.SeedTestUserAsync();
        await ConsentClientAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // Drive a real authorize request so the offer is recorded the way production records it.
        var authorize = await _client.GetAsync(
            "/connect/authorize?client_id=" + ConsentClientId
            + "&response_type=code&redirect_uri=" + Uri.EscapeDataString("https://app.test/callback")
            + "&scope=" + Uri.EscapeDataString("openid profile")
            + "&state=xyz&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Contains("/consent", authorize.Headers.Location!.ToString(), StringComparison.Ordinal);

        // The user unticks `profile`: granted narrows, offered still records both, so the client is
        // not re-prompted for the scope they deliberately declined.
        var response = await _client.PostAsJsonAsync("/consent", new
        {
            clientId = ConsentClientId,
            decision = "allow",
            scopes = new[] { "openid" },
            returnUrl = "/connect/authorize?client_id=" + ConsentClientId + "&scope=openid",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReadConsentAsync(user.Id);
        Assert.NotNull(stored);
        Assert.Equal(["openid"], stored!.Scopes ?? []);
        Assert.Contains("profile", stored.OfferedScopes ?? []);
    }

    // -----------------------------------------------------------------------
    // F67 — the advertised device verification_uri resolves
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AdvertisedVerificationUri_ReachesTheCodeEntryPage()
    {
        await EnableDeviceGrantAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
            ["scope"] = "openid",
        });
        var device = await _client.PostAsync("/connect/deviceauthorization", form);
        device.EnsureSuccessStatusCode();
        var json = await device.Content.ReadFromJsonAsync<JsonElement>();

        var verificationUri = json.GetProperty("verification_uri").GetString()!;
        var path = new Uri(verificationUri).AbsolutePath;

        // The URI was hard-coded to {issuer}/device while the login app mounts BrowserRouter with
        // basename="/login", so the code-entry page lives at /login/device — and no server route for
        // /device existed either. The SPA fallback served the shell, and a BrowserRouter whose
        // basename does not prefix the pathname renders nothing: a user who typed the URL exactly as
        // instructed got a blank page with nowhere to enter the code.
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login/device", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task VerificationUriComplete_CarriesTheUserCodeThrough()
    {
        await EnableDeviceGrantAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
            ["scope"] = "openid",
        });
        var device = await _client.PostAsync("/connect/deviceauthorization", form);
        var json = await device.Content.ReadFromJsonAsync<JsonElement>();

        var complete = new Uri(json.GetProperty("verification_uri_complete").GetString()!);
        var response = await _client.GetAsync(complete.PathAndQuery);

        // The whole point of verification_uri_complete is that the user does not retype the code.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/login/device?user_code=", location, StringComparison.Ordinal);
        Assert.Contains(json.GetProperty("user_code").GetString()!, Uri.UnescapeDataString(location), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private const string ConsentClientId = "consent-client";

    /// <summary>The admin client does not hold the device grant by default.</summary>
    private async Task EnableDeviceGrantAsync()
    {
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId);
        client!.AllowedGrantTypes = ["client_credentials", Authagonal.Core.Constants.GrantTypes.DeviceCode];
        client.AllowedScopes = ["openid", AuthagonalTestFactory.AdminScope];
        await _factory.ClientStore.UpsertAsync(client);
    }

    private Task ConsentClientAsync() => _factory.ClientStore.UpsertAsync(new OAuthClient
    {
        ClientId = ConsentClientId,
        ClientName = "Consent Client",
        RequireClientSecret = false,
        RequirePkce = true,
        RequireConsent = true,
        AllowedGrantTypes = ["authorization_code"],
        RedirectUris = ["https://app.test/callback"],
        AllowedScopes = ["openid", "profile", "email"],
        AccessTokenLifetimeSeconds = 3600,
    });

    private async Task<ConsentView?> ReadConsentAsync(string subjectId)
    {
        var grant = await _factory.GrantStore.GetAsync($"consent:{subjectId}:{ConsentClientId}");
        return grant is null
            ? null
            : JsonSerializer.Deserialize<ConsentView>(grant.Data,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private sealed class ConsentView
    {
        public List<string>? Scopes { get; set; }
        public List<string>? OfferedScopes { get; set; }
    }
}
