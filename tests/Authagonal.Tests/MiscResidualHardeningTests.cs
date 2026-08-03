using System.Text;
using Authagonal.Backup;
using Authagonal.Core.Clustering;
using Authagonal.Server;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Authagonal.Tests;

/// <summary>
/// Cluster configuration that was documented, bound, and then read by nobody.
/// </summary>
public sealed class ClusterOptionsAreHonouredTests
{
    private static IServiceCollection Build(Dictionary<string, string?> settings, Action<ClusteringBuilder>? configure)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthagonalClustering(configuration, configure, runLeaderElection: false);
        return services;
    }

    /// <summary>
    /// <c>Cluster:Enabled=false</c> documents "runs standalone (always leader, in-process event bus)".
    /// Leader election honoured it; the bus did not — the backend callback still replaced the
    /// in-process bus, so a node an operator had taken out of the cluster kept polling the shared
    /// event log and acting on other nodes' invalidations.
    /// </summary>
    [Fact]
    public void Disabled_cluster_keeps_the_in_process_bus()
    {
        var services = Build(
            new Dictionary<string, string?> { ["Cluster:Enabled"] = "false" },
            builder => builder.Services.Replace(
                ServiceDescriptor.Singleton<IClusterEventBus>(new StubClusterEventBus())));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<InProcessClusterEventBus>(provider.GetRequiredService<IClusterEventBus>());
    }

    /// <summary>The switch must not be a blanket refusal: the default (enabled) still wires the backend.</summary>
    [Fact]
    public void Enabled_cluster_still_installs_the_backend_bus()
    {
        var services = Build(
            new Dictionary<string, string?>(),
            builder => builder.Services.Replace(
                ServiceDescriptor.Singleton<IClusterEventBus>(new StubClusterEventBus())));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<StubClusterEventBus>(provider.GetRequiredService<IClusterEventBus>());
    }

    /// <summary>
    /// <c>Cluster:PollIntervalSeconds</c> had no consumer at all: every backend fell straight to its own
    /// hard-coded three seconds, so the setting changed nothing and nothing said so.
    /// </summary>
    [Fact]
    public void Poll_interval_reaches_the_backend()
    {
        TimeSpan? seen = null;
        Build(
            new Dictionary<string, string?> { ["Cluster:PollIntervalSeconds"] = "17" },
            builder => seen = builder.PollInterval);

        Assert.Equal(TimeSpan.FromSeconds(17), seen);
    }

    private sealed class StubClusterEventBus : IClusterEventBus
    {
        public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
            => Task.CompletedTask;

        public IDisposable Subscribe(string topic, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler)
            => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}

/// <summary>
/// The credentialed CORS origin list is pooled from the client table and cached for
/// <c>Cache:CorsCacheMinutes</c> — 60 by default — and nothing invalidated it. Disabling a compromised
/// client, or removing an origin from one, therefore left that origin able to make credentialed
/// cross-origin calls to the protocol surface for up to an hour on every node with a warm entry.
/// </summary>
public sealed class CorsCacheInvalidationTests : IAsyncDisposable
{
    private const string Origin = "https://rp.example";
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static HttpRequestMessage Preflight(string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/connect/token");
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", "POST");
        return req;
    }

    [Fact]
    public async Task Disabling_a_client_drops_its_origin_without_waiting_for_the_cache_to_expire()
    {
        await _factory.SeedTestDataAsync();
        var stored = (await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!;
        stored.AllowedCorsOrigins = [Origin];
        await _factory.ClientStore.UpsertAsync(stored);

        var http = _factory.CreateClient(new() { AllowAutoRedirect = false });

        // Warm the cache.
        Assert.True((await http.SendAsync(Preflight(Origin))).Headers.Contains("Access-Control-Allow-Origin"));

        // Revoke: disable the client, exactly as an operator would on a compromise.
        stored.Enabled = false;
        await _factory.ClientStore.UpsertAsync(stored);

        // Still honoured — the entry is warm, and this is the state that used to last an hour.
        Assert.True((await http.SendAsync(Preflight(Origin))).Headers.Contains("Access-Control-Allow-Origin"));

        await DynamicCorsPolicyProvider.InvalidateAsync(
            _factory.Services.GetRequiredService<IClusterEventBus>(),
            _factory.Services.GetService<Authagonal.Core.Services.ITenantContext>());

        Assert.False((await http.SendAsync(Preflight(Origin))).Headers.Contains("Access-Control-Allow-Origin"),
            "a disabled client's origin was still credentialed after the invalidation");
    }
}

/// <summary>
/// The DataProtection key ring on the managed-identity Azure path — the configuration production is
/// told to use — attached no repository at all, so the ring was the per-machine file store: destroyed
/// on restart, never shared between pods.
/// </summary>
public sealed class DataProtectionBlobUriDerivationTests
{
    [Theory]
    [InlineData("https://acct.table.core.windows.net/", "https://acct.blob.core.windows.net/")]
    [InlineData("https://acct.table.core.windows.net", "https://acct.blob.core.windows.net/")]
    [InlineData("https://acct.table.core.chinacloudapi.cn/", "https://acct.blob.core.chinacloudapi.cn/")]
    public void Derives_the_sibling_blob_endpoint(string tableUri, string expected)
        => Assert.Equal(expected, DataProtectionBlobUri.BlobServiceUriFor(tableUri)?.ToString());

    /// <summary>
    /// Anything that is not a recognisable account-scoped Azure table endpoint is left alone rather
    /// than guessed at: a wrong URI would be a startup failure on a path that works today.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://127.0.0.1:10002/devstoreaccount1")]   // Azurite: path-style, no service name
    [InlineData("https://127.0.0.1:10002/devstoreaccount1")]
    [InlineData("not a uri")]
    [InlineData("http://acct.table.core.windows.net/")]        // cleartext — not a managed-identity endpoint
    public void Leaves_unrecognised_endpoints_alone(string? tableUri)
        => Assert.Null(DataProtectionBlobUri.BlobServiceUriFor(tableUri));
}

/// <summary>
/// Restore-time integrity. Every case here is decided from the manifest and the archive alone —
/// before any table is touched — so none of it needs a storage backend.
/// </summary>
public sealed class RestoreIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"authagonal-restore-tests-{Guid.NewGuid():N}");
    private const string BackupId = "20260801-000000";

    // Never reached: every assertion below throws while reading the archive.
    private readonly Azure.Data.Tables.TableServiceClient _svc =
        new("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Writes a backup directory and returns the manifest that describes it.</summary>
    private BackupManifest Write(params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(_root, BackupId);
        Directory.CreateDirectory(dir);

        var manifest = new BackupManifest { BackupId = BackupId, Mode = "full" };
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
            manifest.FileHashes[name] = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        }
        return manifest;
    }

    private void SaveManifest(BackupManifest manifest)
        => File.WriteAllText(
            Path.Combine(_root, BackupId, "_manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));

    private Task<RestoreResult> RunAsync(RestoreOptions options)
        => new RestoreService(_svc, new FileSystemBackupSource(_root), options).RunAsync(BackupId);

    /// <summary>
    /// Authentication that fails open authenticates nothing. Without a key the hashes only prove the
    /// archive matches a manifest sitting beside it on the same target, so whoever rewrote
    /// Clients.jsonl.gz rewrote its recorded hash in the same breath — and the restore printed a line
    /// to stderr and carried on, which inside a host process or a pipeline is nobody's decision.
    /// </summary>
    [Fact]
    public async Task Missing_manifest_key_is_refused_rather_than_warned_about()
    {
        SaveManifest(Write(("Users.jsonl", "{}")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new RestoreOptions()));
        Assert.Contains("ManifestKey", ex.Message);
    }

    /// <summary>The opt-out exists so a genuinely old, unsigned archive is not unrestorable.</summary>
    [Fact]
    public async Task Explicit_opt_out_still_restores()
    {
        // A real table from the backup set, so the archive passes the destination allowlist; the Tables
        // filter is what excludes it, which is the thing under test. This used to name "Nothing.jsonl" with
        // a comment claiming ExtractTableName could not name it — it can, and the restore therefore chose
        // its destination from the archive.
        SaveManifest(Write(("Users.jsonl", "")));

        var result = await RunAsync(new RestoreOptions
        {
            AllowUnauthenticatedManifest = true,
            Tables = ["NotPresent"],
        });
        Assert.Equal(0, result.TotalRestored);
    }

    /// <summary>
    /// An archive naming a table outside the backup set is refused, not created.
    /// </summary>
    /// <remarks>
    /// The destination was derived purely from the file name — <c>ExtractTableName(fileName)</c> then
    /// <c>GetTableClient(prefix + tableName)</c> with <c>CreateIfNotExists</c> — and
    /// <c>RestoreOptions.Tables</c> is null by default with the CLI leaving it unset. So the set of tables a
    /// restore wrote was chosen entirely by the archive, which could create and populate anything it liked in
    /// the target account.
    /// </remarks>
    [Fact]
    public async Task An_archive_naming_an_unknown_table_is_refused()
    {
        SaveManifest(Write(("EvilSideTable.jsonl", "{}")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new RestoreOptions
        {
            AllowUnauthenticatedManifest = true,
        }));

        Assert.Contains("EvilSideTable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>SigningKeys</c> file is skipped unless the restore explicitly opts in.
    /// </summary>
    /// <remarks>
    /// <c>BackupOptions.IncludeSigningKeys</c> is off by default and <c>BackupService</c> refuses to write
    /// JWT signing private keys — restore honoured no such decision, so an archive carrying the file anyway
    /// installed signing keys into a live deployment. One half of a pair of switches is not a switch. The
    /// skip is reported rather than silent, because "the restore was complete" is the wrong reading and it
    /// only shows up later, when tokens minted under the old key stop validating.
    /// </remarks>
    [Fact]
    public async Task Signing_keys_are_skipped_unless_explicitly_included()
    {
        SaveManifest(Write(("SigningKeys.jsonl", "")));

        var result = await RunAsync(new RestoreOptions { AllowUnauthenticatedManifest = true });

        Assert.True(result.SkippedSigningKeys);
        Assert.Equal(0, result.TotalRestored);
    }

    /// <summary>
    /// A signed manifest is checked. Rewriting the archive means rewriting the hash, which breaks the
    /// MAC — that is the whole point of the key living outside the target.
    /// </summary>
    [Fact]
    public async Task Tampered_manifest_fails_authentication()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var manifest = Write(("Users.jsonl", "{}"));
        ManifestAuthentication.Sign(manifest, key);
        manifest.FileHashes["Users.jsonl"] = new string('0', 64); // rewritten after signing
        SaveManifest(manifest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(new RestoreOptions { ManifestKey = key }));
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deleting a file was invisible: the restore loop iterates what the SOURCE offers, so removing
    /// Clients.jsonl.gz outright — or the tombstone file carrying a set of GDPR erasures — produced a
    /// restore that verified everything it found and reported success.
    /// </summary>
    [Fact]
    public async Task File_listed_in_the_manifest_but_deleted_from_the_store_is_detected()
    {
        var manifest = Write(("Users.jsonl", "{}"));
        SaveManifest(manifest);
        File.Delete(Path.Combine(_root, BackupId, "Users.jsonl"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(new RestoreOptions { AllowUnauthenticatedManifest = true }));
        Assert.Contains("not present", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A rewritten data file is caught, and caught from the same read the entities would be applied
    /// from — verification no longer hashes one read and applies another.
    /// </summary>
    [Fact]
    public async Task Rewritten_data_file_is_refused()
    {
        var manifest = Write(("Users.jsonl", "{}"));
        SaveManifest(manifest);
        File.WriteAllText(Path.Combine(_root, BackupId, "Users.jsonl"), "{\"injected\":true}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(new RestoreOptions { AllowUnauthenticatedManifest = true }));
        Assert.Contains("hash does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Recovery codes written before the KDF change: one unsalted SHA-256 of a 40-bit code, so a single
/// GPU pass over a store read recovered every enrolled user's live codes at once. They kept verifying
/// and nothing was ever going to remove them, because a user who does not exhaust their codes never
/// regenerates.
/// </summary>
public sealed class RecoveryCodeLegacyUpgradeTests
{
    private readonly RecoveryCodeService _service = new(new PasswordHasher());

    private static string LegacyDigest(string code)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(code.Replace("-", "").Replace(" ", "").ToUpperInvariant())));

    [Fact]
    public void Legacy_digest_upgrades_without_the_plaintext_and_still_verifies()
    {
        const string code = "ABCDE-FGHJK";
        var legacy = LegacyDigest(code);
        Assert.True(_service.VerifyCode(code, legacy), "precondition: the legacy form verifies");

        // The KDF is applied to the DIGEST, so no plaintext is needed — which is what makes this a
        // migration a running deployment can actually perform.
        var upgraded = _service.UpgradeLegacyHash(legacy);
        Assert.NotNull(upgraded);
        Assert.DoesNotContain(legacy, upgraded);

        Assert.True(_service.VerifyCode(code, upgraded!), "the user's printed code must keep working");
        Assert.False(_service.VerifyCode("ZZZZZ-ZZZZZ", upgraded!));
    }

    [Fact]
    public void Current_hashes_are_left_alone()
    {
        var current = _service.HashForStorage("ABCDE-FGHJK");
        Assert.Null(_service.UpgradeLegacyHash(current));
        Assert.True(_service.VerifyCode("ABCDE-FGHJK", current));
    }
}
