using Authagonal.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Rate-limit key handling: the residuals the pass over this review's own fixes found here.
/// </summary>
public sealed class RateLimitKeyTests
{
    private sealed class RecordingStore : IRateLimitCounterStore
    {
        public readonly List<string> Keys = [];

        public Task<long> IncrementAsync(string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default)
        {
            Keys.Add(bucketKey);
            return Task.FromResult(1L);
        }
    }

    private static (DurableRateLimiter Limiter, RecordingStore Store) Build()
    {
        var store = new RecordingStore();
        return (new DurableRateLimiter(store, NullLogger<DurableRateLimiter>.Instance), store);
    }

    /// <summary>
    /// A key carrying characters no backend accepts is rewritten before it becomes a storage key.
    /// </summary>
    /// <remarks>
    /// Bucket keys are built from unvalidated caller input at anonymous call sites and reached the backend
    /// verbatim as a partition key. Azure Table forbids <c>/ \ # ?</c> and control characters there, so
    /// <c>POST /saml/a%23b/acs</c> produced a key the store rejected with 400 InvalidInput on every attempt,
    /// forever — and a permanent, deterministic rejection took the limiter's fail-open branch and logged at
    /// Error each time. An attacker-driven flood of store errors, and proof the fail-open needed no outage.
    /// </remarks>
    [Theory]
    [InlineData("saml-acs|a#b|1.2.3.4")]
    [InlineData("saml-acs|a/b|1.2.3.4")]
    [InlineData("saml-acs|a\\b|1.2.3.4")]
    [InlineData("saml-acs|a?b|1.2.3.4")]
    [InlineData("login|id|badcontrol")]
    public async Task AStorageHostileKeyIsRewrittenBeforeItReachesTheStore(string key)
    {
        var (limiter, store) = Build();

        await limiter.IsRateLimitedAsync(key, 5, TimeSpan.FromMinutes(1));

        var sent = Assert.Single(store.Keys);
        Assert.DoesNotContain('#', sent);
        Assert.DoesNotContain('/', sent);
        Assert.DoesNotContain('\\', sent);
        Assert.DoesNotContain('?', sent);
        Assert.DoesNotContain(sent, char.IsControl);
    }

    /// <summary>
    /// Two keys that sanitise to the same text must not become one budget.
    /// </summary>
    /// <remarks>
    /// Substituting a forbidden character for <c>_</c> makes <c>a#b</c> and <c>a_b</c> identical, which would
    /// fold two distinct sources into one bucket — a rate-limit budget shared by parties with nothing to do
    /// with each other. A hash of the ORIGINAL is appended whenever the rewrite changed anything.
    /// </remarks>
    [Fact]
    public async Task TwoKeysThatSanitiseAlikeStayDistinct()
    {
        var (limiter, store) = Build();

        await limiter.IsRateLimitedAsync("saml-acs|a#b", 5, TimeSpan.FromMinutes(1));
        await limiter.IsRateLimitedAsync("saml-acs|a_b", 5, TimeSpan.FromMinutes(1));

        Assert.Equal(2, store.Keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>An ordinary key passes through unchanged, so keys stay readable in logs.</summary>
    [Fact]
    public async Task AnOrdinaryKeyIsNotRewritten()
    {
        var (limiter, store) = Build();

        await limiter.IsRateLimitedAsync("login|ip|203.0.113.9", 5, TimeSpan.FromMinutes(1));

        Assert.StartsWith("login|ip|203.0.113.9|", Assert.Single(store.Keys), StringComparison.Ordinal);
    }

    /// <summary>
    /// The in-process limiter matches keys ORDINALLY, as every durable backend does.
    /// </summary>
    /// <remarks>
    /// It used <c>OrdinalIgnoreCase</c>, so turning on <c>Auth:DurableRateLimiting</c> silently changed what
    /// counts as the same budget: two keys differing only in case shared one bucket before the switch and
    /// became two after it. Nothing was exploitable at the current call sites — each either lowercases its
    /// caller-supplied text or takes it from an ordinal store read — but that made the safety of all of them
    /// an accident of a comparer two layers away rather than a property of the limiter, and the first future
    /// call site keying on unnormalised input would have got a free budget multiplier no test could show.
    /// </remarks>
    [Fact]
    public async Task TheInProcessLimiterMatchesKeysOrdinally()
    {
        var limiter = new InProcessRateLimiter();

        // One attempt allowed. Spending it on the lowercase key must not throttle the uppercase one.
        Assert.False(await limiter.IsRateLimitedAsync("k|alice", 1, TimeSpan.FromMinutes(1)));
        Assert.True(await limiter.IsRateLimitedAsync("k|alice", 1, TimeSpan.FromMinutes(1)));

        Assert.False(await limiter.IsRateLimitedAsync("k|Alice", 1, TimeSpan.FromMinutes(1)));
    }
}

/// <summary>
/// The <c>WWW-Authenticate</c> escaping, whose own comment promised more than it delivered.
/// </summary>
/// <remarks>
/// All three challenge sites replaced only the double quote, under a comment claiming the escaping "keeps
/// that true of the next one somebody adds". Two cases a fixed literal never exercises: a value ending in a
/// backslash escapes the closing quote (RFC 9110 §5.6.4 quoted-pair) and merges the header terminator into
/// the value, and a CR or LF is refused by Kestrel's header validation — so an endpoint whose whole purpose
/// at that moment is to return a well-formed refusal answers 500 from an unhandled exception instead.
/// </remarks>
public sealed class ChallengeHeaderEscapingTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has \"quotes\"", "has \\\"quotes\\\"")]
    [InlineData("trailing\\", "trailing\\\\")]
    [InlineData("a\\\"b", "a\\\\\\\"b")]
    [InlineData("line\r\nbreak", "line  break")]
    [InlineData("bell", "bell ")]
    public void ADescriptionIsRenderedAsAValidQuotedString(string input, string expected)
        => Assert.Equal(expected, Authagonal.Protocol.Endpoints.UserinfoEndpoint.QuotedString(input));

    /// <summary>
    /// The escaped form can be assigned to a real response header without throwing, and injects nothing.
    /// </summary>
    /// <remarks>
    /// The assertion that matters for the CR/LF case: Kestrel validates on assignment, so this is what
    /// distinguishes "escaped" from "escaped enough".
    /// </remarks>
    [Fact]
    public void TheEscapedFormIsAcceptedAsAResponseHeader()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var hostile = "broke\r\nInjected: yes\\";

        var escaped = Authagonal.Protocol.Endpoints.UserinfoEndpoint.QuotedString(hostile);
        ctx.Response.Headers.WWWAuthenticate = $"Bearer realm=\"userinfo\", error_description=\"{escaped}\"";

        var written = ctx.Response.Headers.WWWAuthenticate.ToString();
        Assert.DoesNotContain('\r', written);
        Assert.DoesNotContain('\n', written);
        Assert.False(ctx.Response.Headers.ContainsKey("Injected"));
    }
}
