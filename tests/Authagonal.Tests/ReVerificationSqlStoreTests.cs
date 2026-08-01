using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.SqlProvider;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// The 2026-08-01 comparative re-run found these still open after being reported fixed. Each one is a
// half-landed fix: the successor calculation was made surrogate-aware but its CALLER still dropped
// the bound; the prefix slicing was made rune-safe in the SQL store but not in its two twins; the
// TOTP replay check was written but never made atomic in any provider.
// -------------------------------------------------------------------------------------------------

/// <summary>
/// #236 — a prefix range whose upper bound cannot be computed must not silently become an unbounded
/// scan, and the prefix schemes the three backends share must slice in the same place.
/// </summary>
public sealed class PrefixBoundTests : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    public Task InitializeAsync()
    {
        _source = SqlTestSource.Sqlite();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    private async Task<SqlTable> SeededTableAsync(string name)
    {
        await _source.EnsureTableAsync(name);
        var table = new SqlTable(_source, name);
        foreach (var key in new[] { "AA", "AB", "BA", "BB", "CA", "ZZ" })
            await table.PutAsync(new SqlRow(key, key) { Data = key });
        return table;
    }

    [Fact]
    public async Task PkPrefixEndingInALoneSurrogate_MatchesNothingRatherThanScanningTheTail()
    {
        var table = await SeededTableAsync("PrefixPk");

        // "A" + an unpaired high surrogate — three URL-encoded bytes in a search box. UpperBound has
        // no successor to compute for it, so the caller used to omit `pk < …` entirely and the seek
        // became `pk >= 'A\uD800'`: every key from there to the end of the table.
        var rows = await Collect(table, new SqlKeyFilter { PkPrefix = "A\ud800" });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task SkPrefixEndingInALoneSurrogate_MatchesNothingRatherThanScanningTheTail()
    {
        var table = await SeededTableAsync("PrefixSk");

        var rows = await Collect(table, new SqlKeyFilter { SkPrefix = "A\udc00" });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task AWellFormedPrefix_StillSeeksItsOwnRange()
    {
        // The guard must not be a blanket refusal: the ordinary case has to keep working, and the
        // U+FFFF case (the finding's original trigger) has to stay bounded rather than open.
        var table = await SeededTableAsync("PrefixOk");

        Assert.Equal(["AA", "AB"], (await Collect(table, new SqlKeyFilter { PkPrefix = "A" })).Select(r => r.Pk));
        Assert.Empty(await Collect(table, new SqlKeyFilter { PkPrefix = "A￿" }));
    }

    private static async Task<List<SqlRow>> Collect(SqlTable table, SqlKeyFilter filter)
    {
        var rows = new List<SqlRow>();
        await foreach (var row in table.QueryAsync(filter)) rows.Add(row);
        return rows;
    }
}

/// <summary>
/// #236 — the rune-boundary slicing the prefix index depends on. The SQL store got it; the read side
/// and the Azure/AWS stores kept cutting at UTF-16 code units, which is the same defect with the
/// halves swapped: writes filed under one key, lookups computed from another.
/// </summary>
public class TextPrefixTests
{
    [Theory]
    [InlineData("ABCDEF", 2, "AB")]
    [InlineData("A", 2, "A")]
    [InlineData("", 2, "")]
    // "A" + U+1F600. Two code units for the emoji, so a code-unit cut at 2 leaves a bare high
    // surrogate — a value with no UTF-8 encoding, rejected outright as an Azure Table PartitionKey.
    [InlineData("A\U0001F600B", 2, "A\U0001F600")]
    [InlineData("\U0001F600\U0001F601", 1, "\U0001F600")]
    public void Take_CutsOnScalarBoundaries(string value, int runes, string expected)
    {
        Assert.Equal(expected, TextPrefix.Take(value, runes));
    }

    [Fact]
    public void Take_NeverProducesAnUnpairedSurrogate()
    {
        const string value = "A\U0001F600B\U0001F601C";
        for (var n = 0; n <= 8; n++)
            Assert.True(TextPrefix.IsWellFormed(TextPrefix.Take(value, n)), $"ill-formed at {n} runes");
    }

    [Theory]
    [InlineData("ABC", 3)]
    [InlineData("A\U0001F600B", 3)]
    [InlineData("", 0)]
    public void RuneCount_CountsScalarsNotCodeUnits(string value, int expected)
    {
        Assert.Equal(expected, TextPrefix.RuneCount(value));
    }

    [Fact]
    public void Boundaries_AgreeWithTake()
    {
        const string value = "A\U0001F600BC";
        var boundaries = TextPrefix.Boundaries(value);
        Assert.Equal(TextPrefix.RuneCount(value), boundaries.Count);
        for (var n = 1; n <= boundaries.Count; n++)
            Assert.Equal(TextPrefix.Take(value, n), value[..boundaries[n - 1]]);
    }

    // Built at runtime rather than through [InlineData]: attribute blobs are UTF-8, so a lone
    // surrogate written into one comes back as replacement characters and the case under test
    // evaporates.
    [Fact]
    public void IsWellFormed_RejectsUnpairedSurrogates()
    {
        Assert.True(TextPrefix.IsWellFormed("AB"));
        Assert.True(TextPrefix.IsWellFormed("A\U0001F600"));
        Assert.False(TextPrefix.IsWellFormed("A" + (char)0xD800));            // lone high surrogate
        Assert.False(TextPrefix.IsWellFormed("A" + (char)0xDC00));            // lone low surrogate
        Assert.False(TextPrefix.IsWellFormed($"{(char)0xD800}{(char)0xD800}")); // high followed by high
    }
}

/// <summary>
/// #236 — the same term arriving at the store's own search entry point.
/// </summary>
public sealed class UserSearchTermTests : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    public Task InitializeAsync()
    {
        _source = SqlTestSource.Sqlite();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    [Fact]
    public async Task ASearchTermWithALoneSurrogate_ReturnsNothingAndScansNothing()
    {
        var store = await BuildStoreAsync();
        foreach (var name in new[] { "aaron", "bob", "carla", "zoe" })
            await store.CreateAsync(new AuthUser
            {
                Id = name, Email = $"{name}@x.com", NormalizedEmail = $"{name}@x.com".ToUpperInvariant(),
                FirstName = name, CreatedAt = DateTimeOffset.UtcNow,
            });

        // Before: the email-lookup prefix lost its upper bound and range-scanned from the lone
        // surrogate to the end of the table, so a query that matches nothing returned everyone after
        // it — "A…" returned bob, carla and zoe.
        Assert.Empty(await store.SearchAsync("A" + (char)0xD800));
        Assert.Empty(await store.SearchAsync("B" + (char)0xD800));

        // Still a working prefix search on the same leading characters.
        Assert.Equal(["aaron"], (await store.SearchAsync("aa")).Select(u => u.Id));
    }

    private async Task<SqlUserStore> BuildStoreAsync()
    {
        var tables = await _source.EnsureTablesAsync([
            "Users", "UserEmails", "UserLogins", "UserExternalIds", "UserFirstNames", "UserLastNames",
            "UserEmailDomains", "UserEmailLocalPrefixes",
        ]);
        return new SqlUserStore(
            tables["Users"], tables["UserEmails"], tables["UserLogins"], tables["UserExternalIds"],
            tables["UserFirstNames"], tables["UserLastNames"], EnvPartitioner.Live, null,
            tables["UserEmailDomains"], tables["UserEmailLocalPrefixes"]);
    }
}

/// <summary>
/// #99 — a TOTP time step and a recovery code are single-use, and single-use has to survive requests
/// that overlap. Read-check-write across three round trips gave N concurrent holders of one captured
/// code N successes.
/// </summary>
public sealed class MfaSingleUseClaimTests : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    public Task InitializeAsync()
    {
        _source = SqlTestSource.Sqlite();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    [Fact]
    public async Task VersionCas_RefusesAWriteWhoseReadWasOvertaken()
    {
        // The primitive the claims rest on, tested where the race can be made deterministic: SQLite
        // serializes the store's own round trips, so a 16-way parallel claim converges on one winner
        // even without the predicate. This is what actually fails if the `version = @version` goes.
        await _source.EnsureTableAsync("Cas");
        var table = new SqlTable(_source, "Cas");
        await table.PutAsync(new SqlRow("k", "s") { Data = "v0" });

        var read = await table.GetAsync("k", "s");
        await table.PutAsync(new SqlRow("k", "s") { Data = "someone-else" });   // overtakes the read

        Assert.False(await table.UpdateIfVersionAsync(new SqlRow("k", "s") { Data = "stale" }, read!.Version));
        Assert.Equal("someone-else", (await table.GetAsync("k", "s"))!.Data);

        // And the winner's own version still works, so the guard is not a blanket refusal.
        var fresh = await table.GetAsync("k", "s");
        Assert.True(await table.UpdateIfVersionAsync(new SqlRow("k", "s") { Data = "next" }, fresh!.Version));

        // Never inserts: a row deleted under the writer must stay deleted.
        await table.DeleteAsync("k", "s");
        Assert.False(await table.UpdateIfVersionAsync(new SqlRow("k", "s") { Data = "ghost" }, 0));
        Assert.Null(await table.GetAsync("k", "s"));
    }

    [Fact]
    public async Task TotpStep_HasExactlyOneWinnerUnderConcurrency()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("cred-totp", MfaCredentialType.Totp));

        var claimed = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => store.TryClaimTotpStepAsync("u1", "cred-totp", 58_000_000L)));

        Assert.Equal(1, claimed.Count(c => c));
        Assert.Equal(58_000_000L, (await store.GetCredentialAsync("u1", "cred-totp"))!.LastTotpStep);
    }

    [Fact]
    public async Task TotpStep_RefusesAStaleReadersReplayOfTheSameStep()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("cred-totp", MfaCredentialType.Totp));

        // Exactly the endpoint's sequence with the race spelled out: the attacker's request read the
        // credential (LastTotpStep null, so the code matched) before the victim's request advanced it.
        var stale = await store.GetCredentialAsync("u1", "cred-totp");
        Assert.Null(stale!.LastTotpStep);

        Assert.True(await store.TryClaimTotpStepAsync("u1", "cred-totp", 58_000_000L));
        Assert.False(await store.TryClaimTotpStepAsync("u1", "cred-totp", 58_000_000L));

        // An earlier step is refused too — a code from the previous window is not a fresh claim.
        Assert.False(await store.TryClaimTotpStepAsync("u1", "cred-totp", 57_999_999L));
        // The next window still works, so a legitimate second login is not locked out.
        Assert.True(await store.TryClaimTotpStepAsync("u1", "cred-totp", 58_000_001L));
    }

    [Fact]
    public async Task RecoveryCode_HasExactlyOneWinnerUnderConcurrency()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("cred-rec", MfaCredentialType.RecoveryCode));

        var consumed = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => store.TryConsumeRecoveryCodeAsync("u1", "cred-rec")));

        Assert.Equal(1, consumed.Count(c => c));
        Assert.True((await store.GetCredentialAsync("u1", "cred-rec"))!.IsConsumed);
        Assert.False(await store.TryConsumeRecoveryCodeAsync("u1", "cred-rec"));
    }

    [Fact]
    public async Task AClaimAgainstAMissingCredential_Fails()
    {
        var store = await BuildStoreAsync();
        Assert.False(await store.TryClaimTotpStepAsync("u1", "nope", 1));
        Assert.False(await store.TryConsumeRecoveryCodeAsync("u1", "nope"));
    }

    private static MfaCredential Credential(string id, MfaCredentialType type) => new()
    {
        Id = id, UserId = "u1", Type = type, SecretProtected = "secret", CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// A legacy-hash upgrade must not resurrect a consumed recovery code.
    /// </summary>
    /// <remarks>
    /// The third-pass refuter's catch on #99. The store-level claim was correct, and the recovery handler
    /// then wrote the whole credential row back from a snapshot taken before verification — so a code a
    /// concurrent request had spent came back with <c>IsConsumed = false</c>, and a single-use bypass of the
    /// entire second factor was usable twice. #102's opportunistic upgrade reopened #99 from inside the
    /// same handler, which is why neither branch could see it.
    /// </remarks>
    [Fact]
    public async Task AnUpgradeCannotResurrectAConsumedRecoveryCode()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("r1", MfaCredentialType.RecoveryCode));

        Assert.True(await store.TryConsumeRecoveryCodeAsync("u1", "r1"));

        // The upgrade arrives late, carrying the pre-verification view in which the code was unspent.
        Assert.False(await store.TryUpgradeRecoverySecretAsync("u1", "r1", "upgraded-hash"));

        var after = await store.GetCredentialAsync("u1", "r1");
        Assert.True(after!.IsConsumed);
        Assert.Equal("secret", after.SecretProtected);
    }

    /// <summary>The upgrade still works on a live code — the guard must not make it a no-op.</summary>
    [Fact]
    public async Task AnUnconsumedRecoveryCodeIsUpgradedInPlace()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("r2", MfaCredentialType.RecoveryCode));

        Assert.True(await store.TryUpgradeRecoverySecretAsync("u1", "r2", "upgraded-hash"));

        var after = await store.GetCredentialAsync("u1", "r2");
        Assert.Equal("upgraded-hash", after!.SecretProtected);
        Assert.False(after.IsConsumed);
        // Not a use: the upgrade sweeps the whole set, so stamping LastUsedAt would mark every code as
        // used because one of them was.
        Assert.Null(after.LastUsedAt);
        // And the code is still spendable afterwards.
        Assert.True(await store.TryConsumeRecoveryCodeAsync("u1", "r2"));
    }

    private async Task<SqlMfaStore> BuildStoreAsync()
    {
        var tables = await _source.EnsureTablesAsync(
            ["MfaCredentials", "MfaChallenges", "MfaWebAuthnIndex"]);
        return new SqlMfaStore(
            tables["MfaCredentials"], tables["MfaChallenges"], tables["MfaWebAuthnIndex"], EnvPartitioner.Live);
    }
}

/// <summary>
/// #239 — the SQL backend puts the token-signing private key in the same database, under the same
/// connection string, as everything else. Passthrough stays the supported default; being silent about
/// it did not, given the key ring beside it has always announced itself.
/// </summary>
public sealed class SigningKeyProtectionNoticeTests
{
    private sealed class Capture : ILogger<SigningKeyProtectionCheck>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Authagonal.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class RealCipher : IFieldCipher
    {
        public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default) => Task.FromResult("enc:" + plaintext);
        public Task<string> ResolveAsync(string stored, CancellationToken ct = default) => Task.FromResult(stored);
    }

    [Fact]
    public async Task NoCipherOutsideDevelopment_IsReported()
    {
        var log = new Capture();
        await new SigningKeyProtectionCheck(null, new Env("Production"), log).StartAsync(default);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("SigningKeys", entry.Message, StringComparison.Ordinal);
        Assert.Contains("IFieldCipher", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNullPassthrough_CountsAsNoCipher()
    {
        // Registering NullFieldCipher explicitly is the same plaintext layout as registering nothing;
        // reading the DI entry rather than its behaviour would have called it protected.
        var log = new Capture();
        await new SigningKeyProtectionCheck(NullFieldCipher.Instance, new Env("Production"), log).StartAsync(default);

        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task ACipherOrDevelopment_SaysNothing()
    {
        var withCipher = new Capture();
        await new SigningKeyProtectionCheck(new RealCipher(), new Env("Production"), withCipher).StartAsync(default);
        Assert.Empty(withCipher.Entries);

        // The quick start runs on SQLite with no key management on purpose.
        var dev = new Capture();
        await new SigningKeyProtectionCheck(null, new Env("Development"), dev).StartAsync(default);
        Assert.Empty(dev.Entries);
    }
}

/// <summary>
/// #99 — and the endpoint has to be the thing that consults the claim. With the store's unconditional
/// write suppressed, spending the code is the claim's job alone: if /verify skipped it, the same code
/// would still be good the second time.
/// </summary>
public sealed class MfaVerifySpendsTheCodeTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task TotpVerify_SpendsTheStepThroughTheClaim()
    {
        var user = await SeedMfaUserAsync();
        var secret = await EnrolTotpAsync(user.Id);
        _factory.MfaStore.SuppressBlindCredentialWrites = true;

        var totp = _factory.Services.GetRequiredService<TotpService>();
        var code = totp.GenerateCode(secret);

        Assert.True(await VerifyAsync("totp", code));
        // Same code, same 30-second window, a fresh challenge — which is exactly what an AiTM proxy
        // submits alongside the victim's own login.
        Assert.False(await VerifyAsync("totp", code));
    }

    [Fact]
    public async Task RecoveryVerify_ConsumesTheCodeThroughTheClaim()
    {
        var user = await SeedMfaUserAsync();
        var recoveryService = _factory.Services.GetRequiredService<RecoveryCodeService>();
        const string code = "ABCD-EFGH-IJKL";
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Type = MfaCredentialType.RecoveryCode,
            SecretProtected = recoveryService.HashForStorage(code),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _factory.MfaStore.SuppressBlindCredentialWrites = true;

        Assert.True(await VerifyAsync("recovery", code));
        Assert.False(await VerifyAsync("recovery", code));
    }

    private async Task<AuthUser> SeedMfaUserAsync()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);
        return user;
    }

    private async Task<byte[]> EnrolTotpAsync(string userId)
    {
        var totp = _factory.Services.GetRequiredService<TotpService>();
        var secret = totp.GenerateSecret();
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Type = MfaCredentialType.Totp,
            Name = "Authenticator app",
            SecretProtected = Convert.ToBase64String(secret), // PlaintextSecretProvider in tests
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return secret;
    }

    private async Task<bool> VerifyAsync(string method, string code)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });
        login.EnsureSuccessStatusCode();
        var challengeId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetString();

        var verify = await client.PostAsJsonAsync("/api/auth/mfa/verify",
            new { challengeId, method, code });
        return verify.IsSuccessStatusCode;
    }
}
