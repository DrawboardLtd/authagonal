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

    // ── #99's surviving siblings: the other two blind full-row writes ────────────────────────────

    /// <summary>
    /// Completing an enrolment must not resurrect a credential an administrator revoked mid-request.
    /// </summary>
    /// <remarks>
    /// The TOTP confirm handler claimed its time step conditionally and then wrote the whole row back from
    /// a snapshot taken before the claim, under a comment asserting that carrying the claimed step made the
    /// write safe. <c>UpdateCredentialAsync</c> is an unconditional upsert on every provider, so the write
    /// re-CREATED the row if <c>DeleteAllCredentialsAsync</c> had removed it in between — and the handler
    /// then set <c>MfaEnabled</c> and issued a session cookie. The revoke was undone by the request being
    /// revoked.
    /// </remarks>
    [Fact]
    public async Task ActivatingACredentialCannotResurrectARevokedOne()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("cred-totp", MfaCredentialType.Totp));

        // The enrolment is under way: the step is claimed, and the handler holds a snapshot from before it.
        Assert.True(await store.TryClaimTotpStepAsync("u1", "cred-totp", 58_000_000L));

        // An administrator revokes everything.
        await store.DeleteAllCredentialsAsync("u1");

        Assert.False(await store.TryActivateCredentialAsync("u1", "cred-totp", "Authenticator app"));
        Assert.Null(await store.GetCredentialAsync("u1", "cred-totp"));
    }

    /// <summary>The control: activation names a live pending credential, and touches nothing else.</summary>
    [Fact]
    public async Task ActivatingALiveCredentialNamesItAndLeavesTheClaimedStepAlone()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("cred-totp", MfaCredentialType.Totp));
        Assert.True(await store.TryClaimTotpStepAsync("u1", "cred-totp", 58_000_000L));

        Assert.True(await store.TryActivateCredentialAsync("u1", "cred-totp", "Authenticator app"));

        var after = await store.GetCredentialAsync("u1", "cred-totp");
        Assert.Equal("Authenticator app", after!.Name);
        // The step the claim advanced is still there. The blind write persisted the PRE-claim value, so a
        // concurrent verification's later step could be rolled back and its code put back in play.
        Assert.Equal(58_000_000L, after.LastTotpStep);
        Assert.Equal("secret", after.SecretProtected);
    }

    /// <summary>
    /// A WebAuthn sign counter only ever goes up, and recording a use cannot resurrect a revoked key.
    /// </summary>
    /// <remarks>
    /// The counter is clone detection (WebAuthn §6.1.1): if an authenticator's counter ever goes backwards,
    /// something is replaying. Both assertion paths wrote it back with a full-row upsert of a snapshot read
    /// BEFORE verification, so a captured value could move it down past a concurrent assertion's higher one
    /// — and the upsert re-created a revoked credential, on the passwordless leg where the assertion is the
    /// whole of the authentication.
    /// </remarks>
    [Fact]
    public async Task AWebAuthnSignCounterNeverGoesBackwardsAndNeverResurrects()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("cred-fido", MfaCredentialType.WebAuthn));

        Assert.True(await store.TryRecordWebAuthnUseAsync("u1", "cred-fido", 5));
        // A stale request carrying a lower count is refused rather than rolling the counter back.
        Assert.False(await store.TryRecordWebAuthnUseAsync("u1", "cred-fido", 4));
        Assert.Equal(5u, (await store.GetCredentialAsync("u1", "cred-fido"))!.SignCount);
        // Forward still works.
        Assert.True(await store.TryRecordWebAuthnUseAsync("u1", "cred-fido", 6));

        await store.DeleteAllCredentialsAsync("u1");
        Assert.False(await store.TryRecordWebAuthnUseAsync("u1", "cred-fido", 7));
        Assert.Null(await store.GetCredentialAsync("u1", "cred-fido"));
    }

    /// <summary>
    /// An authenticator that implements no counter reports zero forever, and must still authenticate.
    /// </summary>
    /// <remarks>
    /// The non-vacuity control on the guard above. WebAuthn §6.1.1 makes the sign count optional and a
    /// zero means "not supported", so a strictly-increasing guard would refuse every assertion from such an
    /// authenticator — turning a replay defence into a lockout for a whole class of hardware keys.
    /// </remarks>
    [Fact]
    public async Task AZeroSignCounterAuthenticatorIsStillRecorded()
    {
        var store = await BuildStoreAsync();
        await store.CreateCredentialAsync(Credential("cred-zero", MfaCredentialType.WebAuthn));

        Assert.True(await store.TryRecordWebAuthnUseAsync("u1", "cred-zero", 0));
        Assert.True(await store.TryRecordWebAuthnUseAsync("u1", "cred-zero", 0));

        var after = await store.GetCredentialAsync("u1", "cred-zero");
        Assert.Equal(0u, after!.SignCount);
        // Recorded as a use, which is what the caller needs — the LastUsedAt stamp is the claim's job.
        Assert.NotNull(after.LastUsedAt);
    }

    [Fact]
    public async Task TheNewClaimsAgainstAMissingCredential_Fail()
    {
        var store = await BuildStoreAsync();
        Assert.False(await store.TryRecordWebAuthnUseAsync("u1", "nope", 1));
        Assert.False(await store.TryActivateCredentialAsync("u1", "nope", "name"));
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

/// <summary>
/// #115 — the caller-revision guard must not be handed a fresh revision by the code that just wrote.
/// </summary>
/// <remarks>
/// The store-level guard for #115 was real on all three persistent providers.
/// <c>TccProvisioningOrchestrator.PersistMergeAsync</c> then copied the post-merge revision onto the
/// caller's instance, so the guard had nothing to object to while every other field on that instance was
/// still the copy read BEFORE the provisioning round-trip — a network call to a downstream app. A write
/// through it reverted whatever landed during that round-trip: an admin password reset, a lockout, a
/// profile edit. The fix and the hole were in the same commit, which is why no branch caught it; an
/// adversarial refuter in the third pass did.
/// <para>
/// Against SQLite rather than the in-memory double on purpose: <c>InMemoryUserStore</c> leaves
/// <c>ConcurrencyToken</c> null — the documented fail-open for non-persistent stores — so through it a
/// laundered revision is invisible and this test would assert nothing.
/// </para>
/// </remarks>
public sealed class ProvisioningMergeRevisionTests : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    public Task InitializeAsync()
    {
        _source = SqlTestSource.Sqlite();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    /// <summary>Answers /try with a merge payload, so PersistMergeAsync actually runs.</summary>
    private sealed class MergingTryHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(request.RequestUri!.AbsolutePath == "/try"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"approved":true,"organizationId":"org-1"}""",
                        System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class Factory(System.Net.Http.HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class OneApp : Authagonal.Core.Services.IProvisioningAppProvider
    {
        public Task<IReadOnlyList<Authagonal.Core.Services.ProvisioningApp>> GetAppsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Authagonal.Core.Services.ProvisioningApp>>(
                [new Authagonal.Core.Services.ProvisioningApp("app1", "https://app1.test", null)]);
    }

    /// <summary>
    /// Every caller that writes after a provisioning merge must re-read first.
    /// </summary>
    /// <remarks>
    /// Removing the revision copy (the #115 fix) broke self-service registration and the whole suite
    /// stayed green, because <c>AuthagonalTestFactory</c> substitutes a <c>TestProvisioningOrchestrator</c>
    /// — so no endpoint test ever runs the real one, and the merge that makes the caller's instance stale
    /// never happens. The orchestrator's own comment named the caller: "Exactly ONE caller saved
    /// afterwards — self-service registration."
    /// <para>
    /// This pins the invariant at the orchestrator rather than at one endpoint: after a merge, the
    /// caller's instance is refused by the store. Any future caller that writes without re-reading fails
    /// here instead of in production.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInstanceHeldAcrossAMergeIsRefusedByTheStore()
    {
        var userStore = await BuildUserStoreAsync();
        await userStore.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "ada@acme.test", NormalizedEmail = "ADA@ACME.TEST",
            FirstName = "Ada", CreatedAt = DateTimeOffset.UtcNow,
        });

        var caller = await userStore.GetAsync("u1");
        await NewOrchestrator().ReprovisionAsync(caller!);

        // The write a caller would make if it did NOT re-read. This is the failure registration hit.
        caller!.UpdatedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() => userStore.UpdateAsync(caller));

        // Re-reading first is the supported shape, and it works.
        var fresh = await userStore.GetAsync("u1");
        fresh!.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(fresh);
        Assert.Equal("org-1", (await userStore.GetAsync("u1"))!.OrganizationId);
    }

    [Fact]
    public async Task PersistingAMergeDoesNotHandTheCallerAFreshRevision()
    {
        var userStore = await BuildUserStoreAsync();
        await userStore.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "ada@acme.test", NormalizedEmail = "ADA@ACME.TEST",
            FirstName = "Ada", CreatedAt = DateTimeOffset.UtcNow,
        });

        // What a handler holds across the provisioning round-trip.
        var caller = await userStore.GetAsync("u1");
        var callerRevision = caller!.ConcurrencyToken;
        Assert.False(string.IsNullOrEmpty(callerRevision), "the SQL store must stamp a revision on read");

        await NewOrchestrator().ReprovisionAsync(caller);

        // The merge really was persisted — otherwise the rest of this asserts nothing.
        var stored = await userStore.GetAsync("u1");
        Assert.Equal("org-1", stored!.OrganizationId);

        // And the caller was NOT handed the revision that write produced.
        Assert.Equal(callerRevision, caller.ConcurrencyToken);
        Assert.NotEqual(stored.ConcurrencyToken, caller.ConcurrencyToken);
    }

    /// <summary>
    /// The property the revision exists for: a write built from the pre-round-trip instance is refused
    /// rather than silently reverting what landed during it.
    /// </summary>
    [Fact]
    public async Task AWriteFromThePreRoundTripInstanceIsRefused()
    {
        var userStore = await BuildUserStoreAsync();
        await userStore.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "ada@acme.test", NormalizedEmail = "ADA@ACME.TEST",
            FirstName = "Ada", PasswordHash = "old-hash", CreatedAt = DateTimeOffset.UtcNow,
        });

        var caller = await userStore.GetAsync("u1");

        // An admin resets the password while the provisioning round-trip is in flight.
        var admin = await userStore.GetAsync("u1");
        admin!.PasswordHash = "reset-by-admin";
        await userStore.UpdateAsync(admin);

        await NewOrchestrator().ReprovisionAsync(caller!);

        // Writing the stale instance would put "old-hash" back. It is refused instead.
        caller!.FirstName = "Ada-updated";
        await Assert.ThrowsAsync<InvalidOperationException>(() => userStore.UpdateAsync(caller));

        Assert.Equal("reset-by-admin", (await userStore.GetAsync("u1"))!.PasswordHash);
    }

    private TccProvisioningOrchestrator NewOrchestrator()
    {
        var requestServices = new ServiceCollection()
            .AddSingleton<Authagonal.Core.Stores.IUserProvisionStore>(new InMemoryUserProvisionStore())
            .AddSingleton<Authagonal.Core.Stores.IUserStore>(_ => BuildUserStoreAsync().GetAwaiter().GetResult())
            .BuildServiceProvider();

        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = requestServices },
        };

        return new TccProvisioningOrchestrator(
            new Factory(new MergingTryHandler()),
            accessor,
            new OneApp(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TccProvisioningOrchestrator>.Instance);
    }

    private async Task<SqlUserStore> BuildUserStoreAsync()
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
