using System.Net;
using System.Net.Http.Json;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The limiter that protects everything else must not be the cheapest thing to attack. Its keys embed
/// attacker-chosen values (source addresses, emails, user codes), so an unauthenticated caller mints new
/// keys at will — and the old prune could free nothing while costing O(n) under a process-global lock on
/// every insert past the threshold, because the trigger was a COUNT and the predicate was an AGE.
/// </summary>
public class RateLimiterBoundsTests
{
    /// <summary>The basic contract, so the rewrite did not change limiting behaviour.</summary>
    [Fact]
    public async Task Allows_up_to_the_limit_then_blocks()
    {
        var limiter = new InProcessRateLimiter();
        var window = TimeSpan.FromMinutes(1);

        for (var i = 0; i < 3; i++)
            Assert.False(await limiter.IsRateLimitedAsync("k", 3, window), $"attempt {i + 1}");

        Assert.True(await limiter.IsRateLimitedAsync("k", 3, window));
    }

    [Fact]
    public async Task Distinct_keys_have_independent_budgets()
    {
        var limiter = new InProcessRateLimiter();
        var window = TimeSpan.FromMinutes(1);

        for (var i = 0; i < 5; i++)
            await limiter.IsRateLimitedAsync("a", 1, window);

        Assert.True(await limiter.IsRateLimitedAsync("a", 1, window));
        Assert.False(await limiter.IsRateLimitedAsync("b", 1, window));
    }

    /// <summary>
    /// The memory bound. Ten thousand keys created in quick succession previously hit the count threshold
    /// while nothing matched the 2-hour age predicate, so the dictionary grew without limit AND paid a full
    /// scan per insert. Growth must now be capped whatever the insert pattern.
    /// </summary>
    [Fact]
    public async Task Key_flood_does_not_grow_without_bound()
    {
        // A small cap, so the bound is asserted without inserting tens of thousands of keys (which would
        // also starve timing-sensitive tests running in parallel).
        const int cap = 500;
        var limiter = new InProcessRateLimiter(cap);
        var window = TimeSpan.FromMinutes(10); // long enough that nothing expires during the test

        for (var i = 0; i < cap * 6; i++)
            await limiter.IsRateLimitedAsync($"flood-{i}", 100, window);

        // Bounded, and the bound is enforced rather than aspirational.
        Assert.True(limiter.TrackedWindows <= cap,
            $"tracked {limiter.TrackedWindows} windows against a cap of {cap}; the cap is not enforced");
    }

    /// <summary>
    /// A flood must not become quadratic. Not a wall-clock assertion on absolute speed — it asserts that the
    /// inserts complete in a time a per-insert O(n) scan under a global lock could not.
    /// </summary>
    [Fact]
    public async Task Key_flood_does_not_pay_a_scan_per_insert()
    {
        // 20k inserts against a 2k cap: the old shape walked up to 2k entries per insert past the
        // threshold (~36M comparisons); the amortised one sweeps at most once per interval.
        const int cap = 2_000;
        var limiter = new InProcessRateLimiter(cap);
        var window = TimeSpan.FromMinutes(10);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < cap * 10; i++)
            await limiter.IsRateLimitedAsync($"perf-{i}", 100, window);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"{cap * 10} inserts took {sw.Elapsed}, which suggests an O(n) sweep on the hot path");
    }

    /// <summary>An elapsed window frees its entry rather than being retained until an unrelated age cutoff.</summary>
    [Fact]
    public async Task Elapsed_windows_are_collectable()
    {
        const int cap = 500;
        var limiter = new InProcessRateLimiter(cap);

        // A window this short is already elapsed by the time capacity maintenance next runs.
        for (var i = 0; i < cap * 6; i++)
            await limiter.IsRateLimitedAsync($"short-{i}", 100, TimeSpan.FromMilliseconds(1));

        Assert.True(limiter.TrackedWindows <= cap);
    }
}

/// <summary>
/// Login had no rate limit of any kind. Per-account lockout does not bound spraying — one attempt each
/// against many accounts trips no counter — and because an unknown email is deliberately verified against a
/// dummy hash to keep response timing uniform, every unauthenticated request pays a full PBKDF2, so the
/// endpoint was a CPU amplifier as well as an unthrottled credential oracle.
/// </summary>
public sealed class LoginRateLimitTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Repeated_login_attempts_are_eventually_refused()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Spray DISTINCT unknown addresses: no account lockout can fire, so anything that stops this is the
        // per-source bound rather than the per-account one.
        var sawTooMany = false;
        for (var i = 0; i < 60 && !sawTooMany; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login",
                new { email = $"nobody-{i}@example.com", password = "whatever-Aa1!" });
            sawTooMany = response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        Assert.True(sawTooMany, "unbounded password attempts from one source were accepted");
    }

    /// <summary>A legitimate sign-in must still work — the bound must not break normal use.</summary>
    [Fact]
    public async Task A_normal_login_still_succeeds()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// Forgot-password had a per-EMAIL cap and nothing per source.
/// </summary>
/// <remarks>
/// The per-email cap bounds how much mail one victim gets; it says nothing about a caller working
/// through an address list. From a single source that was unbounded anonymous mail delivery — one
/// message per address, from the tenant's own verified sending domain, with the deliverability damage
/// landing on the tenant — plus one user-store read per attempt whether or not the account exists.
/// Register has carried a per-source cap all along; this endpoint is the same primitive and does not
/// even need an account.
/// </remarks>
public sealed class ForgotPasswordRateLimitTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new()
    {
        // Small enough to assert without sending dozens of mails; the default (15/hour) is a resting
        // place for real NAT egress, not a value worth looping to in a test.
        ConfigureAuthOptions = o => o.MaxPasswordResetsPerIp = 3,
    };

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Repeated_reset_requests_from_one_source_are_eventually_refused()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // DISTINCT addresses, so the per-email cap cannot be what stops this — and none of them exist,
        // so no account-level counter is involved either.
        var sawTooMany = false;
        for (var i = 0; i < 8 && !sawTooMany; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
                new { email = $"victim-{i}@example.com" });
            sawTooMany = response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        Assert.True(sawTooMany, "unbounded password-reset mail from one source was accepted");
    }

    /// <summary>The bound must not break a real user asking for a reset.</summary>
    [Fact]
    public async Task A_single_reset_request_still_sends()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { email = "test@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.EmailService.SentEmails,
            e => e.Email == "test@example.com" && e.Type == "password_reset");
    }
}
