using Authagonal.Server.Services;
using Microsoft.Extensions.Logging;

namespace Authagonal.Tests;

/// <summary>
/// The runtime patch floor: the one security guarantee this server cannot express as a package
/// reference, only as an assertion at startup.
/// </summary>
/// <remarks>
/// GHSA-37gx-xxp4-5rgx and GHSA-w3x6-4m5h-cxqf live in <c>System.Security.Cryptography.Xml</c>, which
/// an <c>Sdk.Web</c> project gets from the shared framework — the SDK prunes any PackageReference to
/// it, so the pin that was supposed to fix them never travelled to a single consumer. What is left is
/// the runtime's own patch level, and both advisories are reachable from the anonymous SAML ACS
/// endpoint.
/// <para>
/// The floor had a second failure mode of its own: it was written as 9.0.15 / 10.0.6, the releases
/// that carried those two advisories, and then three further security releases shipped on each major
/// while the floor stayed where it was. A host on 9.0.16 or 10.0.9 was told nothing. These tests pin
/// the boundary at the current floor, and pin that <c>Auth:RequireMinimumRuntime</c> is the difference
/// between a Critical log and a refusal — a distinction the process has to make correctly, because
/// defaulting to refusal would turn a version bump into an outage.
/// </para>
/// </remarks>
public sealed class RuntimeVersionFloorTests
{
    private sealed class Capture : ILogger<RuntimeVersionFloor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));

        public bool HasCritical => Entries.Any(e => e.Level == LogLevel.Critical);
        public string Text => string.Join("\n", Entries.Select(e => e.Message));
    }

    private static (Capture Log, RuntimeVersionFloor Check) Build(string running, bool require = false)
    {
        var log = new Capture();
        return (log, new RuntimeVersionFloor(log, require, () => Version.Parse(running)));
    }

    // ── the boundary ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The releases between the XML advisories and the current floor are exactly the window the old
    /// 9.0.15 / 10.0.6 floor left unguarded — a host on any of these was running with every fix
    /// published after the XML pair missing, and was told nothing at all.
    /// </summary>
    [Theory]
    [InlineData("9.0.15")]
    [InlineData("9.0.16")]
    [InlineData("9.0.17")]
    [InlineData("10.0.6")]
    [InlineData("10.0.9")]
    public async Task Below_the_floor_is_critical(string running)
    {
        var (log, check) = Build(running);

        await check.StartAsync(CancellationToken.None);

        Assert.True(log.HasCritical);
        Assert.Contains("GHSA-37gx-xxp4-5rgx", log.Text);
        Assert.Contains("GHSA-w3x6-4m5h-cxqf", log.Text);
    }

    [Theory]
    [InlineData("9.0.18")]
    [InlineData("9.0.19")]
    [InlineData("10.0.10")]
    [InlineData("10.0.11")]
    public async Task At_or_above_the_floor_is_silent(string running)
    {
        var (log, check) = Build(running);

        await check.StartAsync(CancellationToken.None);

        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// A major with no floor declared — .NET 11 previews, or a host that has moved past what this
    /// version of Authagonal knows about — is not something to warn about. The floor names the
    /// majors it has verified; silence on an unknown one is correct, and inventing a comparison
    /// against the nearest known major would fire on every new runtime.
    /// </summary>
    [Fact]
    public async Task Unknown_major_is_not_judged()
    {
        var (log, check) = Build("11.0.0");

        await check.StartAsync(CancellationToken.None);

        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// An unparseable framework description must not be the reason an identity provider fails to
    /// boot — availability wins where the alternative is guessing.
    /// </summary>
    [Fact]
    public async Task Unreadable_version_is_not_judged()
    {
        var log = new Capture();
        var check = new RuntimeVersionFloor(log, requireMinimumRuntime: true, () => null);

        await check.StartAsync(CancellationToken.None);

        Assert.Empty(log.Entries);
    }

    // ── refuse versus log ────────────────────────────────────────────────────────

    [Fact]
    public async Task Require_refuses_to_start_below_the_floor()
    {
        var (_, check) = Build("10.0.9", require: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => check.StartAsync(CancellationToken.None));

        Assert.Contains("10.0.10", ex.Message);
        Assert.Contains("Auth:RequireMinimumRuntime", ex.Message);
    }

    /// <summary>
    /// The opt-in must not become an unconditional refusal: a patched host with the switch on has to
    /// start, or turning it on would be a coin toss rather than a policy.
    /// </summary>
    [Fact]
    public async Task Require_starts_normally_at_the_floor()
    {
        var (log, check) = Build("10.0.10", require: true);

        await check.StartAsync(CancellationToken.None);

        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// Default is availability: an out-of-date runtime is stated, loudly, and the server still comes
    /// up. Refusing by default would turn a version bump of this package into an outage on a fleet
    /// whose runtime is one patch behind.
    /// </summary>
    [Fact]
    public async Task Default_does_not_refuse()
    {
        var (log, check) = Build("9.0.15");

        await check.StartAsync(CancellationToken.None);

        Assert.True(log.HasCritical);
        Assert.Contains("Auth:RequireMinimumRuntime", log.Text);
    }
}
