using System.Net.Http.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The two headers a login page most depends on, and the attributes that decide whether its session
/// cookie can be stolen or overwritten.
/// </summary>
public sealed class BrowserHardeningTests : IAsyncLifetime
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
    // F343 — CSP must name the directives that do not inherit default-src
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Csp_ConstrainsBaseUriAndFormAction()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        // Neither falls back to default-src. Without base-uri an injected <base> re-points every
        // relative URL on the page — form posts, script src, API calls — while the policy still reads
        // as locked down; without form-action an injected form posts credentials anywhere, which
        // script-src does not constrain.
        Assert.Contains("base-uri 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // F342 / F246 — the session cookie
    // -----------------------------------------------------------------------
    //
    // NOT covered here, deliberately. AuthagonalTestFactory configures its own cookie scheme rather
    // than going through AddAuthagonal, so the production policy is not what this harness exercises —
    // and it could not be: TestServer speaks HTTP, and CookieContainer refuses to send a Secure
    // cookie over it, so every cookie-dependent test in the suite would break. The factory therefore
    // pins SameAsRequest on purpose (see the note there).
    //
    // The production change — CookieSecurePolicy.Always by default, plus the __Host- prefix — is
    // verifiable only against a real host over TLS. Flagged rather than asserted, because a test that
    // passed here would be testing the harness's copy of the wiring, not the shipped default.
}
