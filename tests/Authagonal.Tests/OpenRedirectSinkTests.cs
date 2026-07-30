using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The open-redirect sinks, exercised through the real endpoints rather than the helper in isolation — the
/// helper being correct is necessary but not sufficient, since the bug was partly that some sinks never
/// called it.
/// </summary>
public sealed class OpenRedirectSinkTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// The SAML ACS error path redirects to RelayState. A tab makes it protocol-relative once the browser
    /// parses it, so the Location header must not carry the attacker's host at all.
    /// </summary>
    [Fact]
    public async Task Saml_acs_error_does_not_redirect_offsite_via_a_tab()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // An invalid SAMLResponse takes the RedirectWithError path, which is the sink.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = "not-valid-base64-xml",
            ["RelayState"] = "/\t/evil.example/",
        });

        var response = await client.PostAsync("/saml/does-not-exist/acs", form);
        var location = response.Headers.Location?.ToString() ?? "";

        Assert.DoesNotContain("evil.example", location);
    }

    /// <summary>
    /// Consent Allow echoed the caller-supplied returnUrl into a value the login app assigns to
    /// window.location.href. A crafted consent URL therefore redirected a signed-in user off-site from the
    /// IdP's own origin on a single click.
    /// </summary>
    [Fact]
    public async Task Consent_allow_does_not_echo_an_offsite_return_url()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });

        foreach (var hostile in new[] { "https://evil.example/", "//evil.example/", "/\t/evil.example/" })
        {
            var response = await client.PostAsJsonAsync("/consent", new
            {
                clientId = AuthagonalTestFactory.TestClientId,
                decision = "allow",
                scopes = new[] { "openid" },
                returnUrl = hostile,
            });

            if (!response.IsSuccessStatusCode) continue;

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var redirect = body.TryGetProperty("redirect", out var r) ? r.GetString() ?? "" : "";
            Assert.DoesNotContain("evil.example", redirect);
        }
    }

    /// <summary>
    /// Consent Deny parsed a redirect_uri out of the attacker's returnUrl and emitted an OAuth error to it
    /// without checking it against the client's registered URIs — an open redirect on the IdP origin, and an
    /// RFC 6749 §4.1.2.1 violation. /connect/authorize has always refused this; the consent path bypassed it.
    /// </summary>
    [Fact]
    public async Task Consent_deny_refuses_an_unregistered_redirect_uri()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });

        var response = await client.PostAsJsonAsync("/consent", new
        {
            clientId = AuthagonalTestFactory.TestClientId,
            decision = "deny",
            returnUrl = "/connect/authorize?client_id=" + AuthagonalTestFactory.TestClientId
                        + "&redirect_uri=https%3A%2F%2Fevil.example%2Fpwn&state=s",
        });

        Assert.True(response.IsSuccessStatusCode, $"unexpected {response.StatusCode}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var redirect = body.TryGetProperty("redirect", out var r) ? r.GetString() ?? "" : "";

        Assert.DoesNotContain("evil.example", redirect);
    }

    /// <summary>
    /// Deny with the client's REGISTERED redirect_uri must still emit the OAuth error there — the guard
    /// must not break the legitimate deny flow.
    /// </summary>
    [Fact]
    public async Task Consent_deny_still_reports_access_denied_to_a_registered_uri()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        var registered = (await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!
            .RedirectUris.FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(registered));

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });

        var response = await client.PostAsJsonAsync("/consent", new
        {
            clientId = AuthagonalTestFactory.TestClientId,
            decision = "deny",
            returnUrl = "/connect/authorize?client_id=" + AuthagonalTestFactory.TestClientId
                        + "&redirect_uri=" + Uri.EscapeDataString(registered!) + "&state=s",
        });

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var redirect = body.GetProperty("redirect").GetString() ?? "";

        Assert.Contains("access_denied", redirect);
        // Compare structurally: UriBuilder re-emits the default port explicitly ("https://app.test:443/..."),
        // which is pre-existing normalization rather than anything this guard changed.
        var actual = new Uri(redirect);
        var expected = new Uri(registered!);
        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.AbsolutePath, actual.AbsolutePath);
    }
}
