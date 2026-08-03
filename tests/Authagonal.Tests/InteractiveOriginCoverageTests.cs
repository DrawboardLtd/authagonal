using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The diff-scoped pass found the origin guard was applied at four sites while the CHANGELOG asserted those
/// were all of them. They were not.
/// </summary>
/// <remarks>
/// <c>POST /consent</c> — the PRIMARY OAuth consent-granting POST, the one that writes the
/// <c>consent:{sub}:{client}</c> grant and the offered-scope set that suppresses future prompts — had no
/// guard. Nor did the whole <c>/api/auth/mfa/*</c> setup group, <c>POST /api/auth/logout</c>,
/// <c>PATCH /api/auth/profile</c>, or either session-revocation route.
/// <para>
/// The sharpest of those is <c>POST /api/auth/mfa/recovery/generate</c>. It binds NO request body, so a
/// <c>fetch(url, {method:'POST', credentials:'include'})</c> sends no <c>Content-Type</c> and is therefore a
/// CORS-SIMPLE request: the browser issues no preflight and the request EXECUTES whatever the CORS policy
/// says — only the response is withheld. The handler deletes the victim's recovery codes and issues ten new
/// ones. Even with the response unreadable, destroying someone's recovery path is the damage; with a
/// credentialed CORS grant the attacker reads the codes too, and each one satisfies
/// <c>/api/auth/mfa/verify</c>.
/// </para>
/// <para>
/// So this file has two halves. Behavioural tests for the routes that were open, and a CONVENTION test for
/// the class — because "four sites, and that is all of them" was a claim about coverage that only a coverage
/// check can keep true.
/// </para>
/// </remarks>
public sealed class InteractiveOriginCoverageTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpRequestMessage CrossOrigin(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        // A sibling origin, which is what SameSite=Lax does NOT withhold the cookie from.
        request.Headers.Add("Origin", "https://app.test");
        return request;
    }

    private HttpRequestMessage SameOrigin(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", AuthagonalTestFactory.TestIssuer);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    /// <summary>
    /// The body-less MFA route: reachable cross-origin with no CORS grant, because no body means no
    /// preflight.
    /// </summary>
    [Fact]
    public async Task RecoveryCodeGeneration_FromAnotherOrigin_IsRefused()
    {
        var response = await _client.SendAsync(CrossOrigin(HttpMethod.Post, "/api/auth/mfa/recovery/generate"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The primary consent grant. Its absence from the guard is what made the CHANGELOG claim false.
    /// </summary>
    [Fact]
    public async Task ConsentPost_FromAnotherOrigin_IsRefused()
    {
        var request = CrossOrigin(HttpMethod.Post, "/consent");
        request.Content = JsonContent.Create(new { clientId = AuthagonalTestFactory.TestClientId, scopes = new[] { "openid" } });

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("POST", "/api/auth/sessions/revoke-others")]
    [InlineData("PATCH", "/api/auth/profile")]
    [InlineData("POST", "/api/auth/mfa/totp/setup")]
    [InlineData("DELETE", "/consent/agents/some-agent")]
    [InlineData("DELETE", "/consent/grants/some-client")]
    public async Task InteractiveStateChange_FromAnotherOrigin_IsRefused(string method, string path)
    {
        var request = CrossOrigin(new HttpMethod(method), path);

        // A body only where the route BINDS one. Endpoint filters run after argument binding, so a route
        // expecting a body answers 400 for a missing one before the guard is reached — a refusal, but not
        // the one being asserted. Sending a body to a route that binds none is equally wrong: it changed
        // /sessions/revoke-others from 403 to 500, which would have looked like the guard not firing.
        if (path is "/api/auth/profile")
            request.Content = JsonContent.Create(new { firstName = "X" });

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// The control: the login app's own origin is not refused, and a GET is never refused.
    /// </summary>
    /// <remarks>
    /// Without this, a guard that returned 403 unconditionally would satisfy every assertion above and would
    /// break the entire account UI. The GET case matters separately — the filter covers a whole group, and a
    /// group with reads in it must keep serving them.
    /// </remarks>
    [Fact]
    public async Task TheLoginAppsOwnOrigin_IsNotRefused()
    {
        var write = await _client.SendAsync(SameOrigin(HttpMethod.Post, "/api/auth/mfa/recovery/generate"));
        Assert.NotEqual(HttpStatusCode.Forbidden, write.StatusCode);

        var read = await _client.SendAsync(CrossOrigin(HttpMethod.Get, "/api/auth/mfa/status"));
        Assert.NotEqual(HttpStatusCode.Forbidden, read.StatusCode);
    }

    // ── The class, not the instances ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every cookie-authenticated state-changing interactive route carries the guard.
    /// </summary>
    /// <remarks>
    /// Anonymous routes are excluded and named: they carry no ambient credential for a cross-origin page to
    /// abuse, and an Origin check on <c>/api/auth/login</c> would break a legitimate cross-origin sign-in
    /// from a first-party SPA. That exclusion is stated per route rather than inferred, because "it does not
    /// need it" is what will also be said about the next route that does.
    /// </remarks>
    [Fact]
    public void EveryInteractiveStateChangingRouteCarriesTheGuard()
    {
        var src = Path.Combine(RepositoryRoot(), "src", "Authagonal.Server", "Endpoints");

        // Groups whose declaration carries RequireOwnOrigin cover every route inside them.
        var guardedGroups = new[] { "/api/auth/mfa" };

        var anonymous = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/login"] = "Anonymous. No ambient credential to abuse, and an Origin check would break a "
                + "legitimate cross-origin sign-in from a first-party SPA.",
            ["/register"] = "Anonymous, same reasoning.",
            ["/forgot-password"] = "Anonymous, same reasoning.",
            ["/reset-password"] = "Anonymous — authorised by the emailed token, not by a cookie.",
            ["/confirm-email"] = "Anonymous — authorised by the emailed token.",
        };

        // Client-authenticated protocol endpoints. These take a client_id and secret (or a client
        // assertion) — there is no ambient cookie for a cross-origin page to attach, so a browser cannot
        // drive them on a victim's behalf at all. Named rather than pattern-matched, so a future
        // cookie-authenticated route under /connect/ is not silently swept up with them.
        var clientAuthenticated = new HashSet<string>(StringComparer.Ordinal)
        {
            "/connect/token", "/connect/revocation", "/connect/introspect",
            "/connect/deviceauthorization", "/connect/par", "/connect/register",
        };

        // Every Map* call, in order — the WINDOW for one route is from its own match to the next one.
        //
        // Matching "everything up to the first semicolon" does not work here and produced false positives on
        // the first run: these routes are inline lambdas, so the first semicolon is inside the handler body,
        // long before the trailing .RequireOwnOrigin(). Same class of mistake as a body boundary that
        // matches its own declaration — a lint with the wrong window teaches people to add exemptions.
        var anyMap = new Regex(@"\w+\.Map(?<verb>Get|Post|Put|Patch|Delete|Methods|Group)\(\s*""(?<route>[^""]*)""",
            RegexOptions.Compiled);

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');

            // The admin and SCIM APIs authenticate with a BEARER token, not an ambient cookie, so a
            // cross-origin page has nothing to attach.
            if (relative.StartsWith("Admin/", StringComparison.Ordinal)
                || relative.StartsWith("Scim/", StringComparison.Ordinal)) continue;

            var text = File.ReadAllText(file);
            var groupIsGuarded = guardedGroups.Any(g =>
                Regex.IsMatch(text, $@"MapGroup\(""{Regex.Escape(g)}""\)[^;]*RequireOwnOrigin"));

            var maps = anyMap.Matches(text).ToList();
            for (var i = 0; i < maps.Count; i++)
            {
                var verb = maps[i].Groups["verb"].Value;
                if (verb is "Get" or "Group") continue;

                var route = maps[i].Groups["route"].Value;
                if (anonymous.ContainsKey(route) || clientAuthenticated.Contains(route)) continue;

                var from = maps[i].Index;
                var to = i + 1 < maps.Count ? maps[i + 1].Index : text.Length;
                var window = text[from..to];

                if (window.Contains("AllowAnonymous", StringComparison.Ordinal)) continue;
                if (window.Contains("RequireOwnOrigin", StringComparison.Ordinal)) continue;
                if (window.Contains("InteractiveOriginGuard", StringComparison.Ordinal)) continue;
                if (groupIsGuarded) continue;

                offenders.Add($"{relative}: {verb} {route}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These interactive state-changing routes carry no origin guard. SameSite=Lax does not withhold "
            + "the session cookie from a same-site cross-ORIGIN request, and a body-less POST is CORS-simple "
            + "— delivered without a preflight whatever the CORS policy says. Add .RequireOwnOrigin(), or "
            + "list the route as anonymous with the reason it has no ambient credential to abuse. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
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
