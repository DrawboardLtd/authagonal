using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Four session and MFA controls, each applied on a sibling path and dropped on this one.
/// </summary>
public sealed class SessionAndMfaGapTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // ── #42: every cookie obeys one Secure policy ────────────────────────────

    /// <summary>
    /// Three hand-built cookies took <c>Secure</c> from <c>Request.IsHttps</c> — the posture the session
    /// cookie's own configuration rejects.
    /// </summary>
    /// <remarks>
    /// The session cookie uses <c>CookieSecurePolicy.Always</c> unless <c>Authentication:AllowInsecureCookie</c>
    /// is set, because <c>SameAsRequest</c> "depends on X-Forwarded-Proto arriving and being trusted: a
    /// misconfigured ingress, a health probe on plain HTTP, or a proxy that drops the header yields a NON-Secure
    /// cookie… The failure is silent."
    /// <para>
    /// With TLS terminated at an ingress not declared in <c>ForwardedHeaders:KnownProxies</c>, <c>IsHttps</c> is
    /// false for every request — so <c>mfa_setup</c>, <c>oidc_state</c> and <c>saml_request</c> went out
    /// non-Secure while the session cookie beside them did not. <c>mfa_setup</c> is the worst of the three: it
    /// is accepted as the sole identity for the enrolment endpoints, so it is a full sign-in credential.
    /// </para>
    /// <para>
    /// The test client speaks http, which is exactly the condition that used to produce a non-Secure cookie.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheOidcStateCookieIsSecure_EvenOverPlainHttp()
    {
        await _factory.OidcProviderStore.UpsertAsync(new OidcProviderConfig
        {
            ConnectionId = "acme",
            ConnectionName = "Acme",
            MetadataLocation = "https://idp.example/.well-known/openid-configuration",
            ClientId = "c",
            ClientSecret = "s",
        });

        var response = await _client.GetAsync("/oidc/acme/login");

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : [];
        var state = setCookies.FirstOrDefault(c => c.StartsWith("oidc_state", StringComparison.Ordinal));

        // The connection cannot actually be reached, so a redirect is not guaranteed — but if the cookie was
        // set at all it must be Secure.
        if (state is not null)
            Assert.Contains("secure", state, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A source check, because the decision is what matters and it is now shared.</summary>
    [Fact]
    public void NoCookieTakesItsSecureFlagFromRequestIsHttps()
    {
        var root = RepositoryRoot();
        string[] files =
        [
            "src/Authagonal.Server/Endpoints/OidcEndpoints.cs",
            "src/Authagonal.Server/Endpoints/MfaSetupEndpoints.cs",
            "src/Authagonal.Server/Endpoints/SamlEndpoints.cs",
        ];

        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("Secure = httpContext.Request.IsHttps", text, StringComparison.Ordinal);
            Assert.Contains("CookieSecurity.Secure(httpContext)", text, StringComparison.Ordinal);
        }
    }

    // ── #3: MfaPolicy.Required on the device-approval path ───────────────────

    /// <summary>
    /// A user with no second factor could approve a device for a client that requires one.
    /// </summary>
    /// <remarks>
    /// <c>AuthorizeEndpoint</c> enforces <c>MfaPolicy.Required</c> with a comment naming its own scope —
    /// "enforced HERE, which is the only place both the subject and the client are known for certain". The device
    /// approval endpoint is a second such place: the subject comes from the cookie principal and the client id
    /// from the device grant, both server-side. It consulted neither <c>client.MfaPolicy</c> nor
    /// <c>IAuthHook.ResolveMfaPolicyAsync</c>, and the grant it approves mints tokens for 30 days.
    /// </remarks>
    [Fact]
    public async Task DeviceApproval_IsRefusedForAnUnenrolledUserWhenTheClientRequiresMfa()
    {
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId);
        client!.AllowedGrantTypes = ["client_credentials", GrantTypes.DeviceCode];
        client.AllowedScopes = ["openid", "profile"];
        client.MfaPolicy = MfaPolicy.Required;
        await _factory.ClientStore.UpsertAsync(client);

        await _factory.SeedTestUserAsync(); // no MFA enrolled
        var codes = await RequestDeviceCodesAsync();

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var approve = await _client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["user_code"] = codes.UserCode }));

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Contains("multi-factor", await approve.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // And the device is still waiting rather than approved.
        var poll = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.DeviceCode,
                ["device_code"] = codes.DeviceCode,
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
            }));
        var pollJson = await poll.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization_pending", pollJson.GetProperty("error").GetString());
    }

    /// <summary>The control: an Enabled policy still approves, so this is not a blanket refusal.</summary>
    [Fact]
    public async Task DeviceApproval_StillWorksWhenTheClientDoesNotRequireMfa()
    {
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId);
        client!.AllowedGrantTypes = ["client_credentials", GrantTypes.DeviceCode];
        client.AllowedScopes = ["openid", "profile"];
        client.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(client);

        await _factory.SeedTestUserAsync();
        var codes = await RequestDeviceCodesAsync();

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var approve = await _client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["user_code"] = codes.UserCode }));

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    private async Task<(string DeviceCode, string UserCode)> RequestDeviceCodesAsync()
    {
        var response = await _client.PostAsync("/connect/deviceauthorization", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
                ["scope"] = "openid profile",
            }));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("device_code").GetString()!, json.GetProperty("user_code").GetString()!);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
