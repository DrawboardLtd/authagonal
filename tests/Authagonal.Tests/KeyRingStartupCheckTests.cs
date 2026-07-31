using System.Xml.Linq;
using Authagonal.SqlProvider;
using Authagonal.SqlProvider.Sql;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// The DataProtection key ring protects the authentication cookie, so a persisted-but-unencrypted ring
/// is the ability to forge a session for any user, held by anyone who can read the store.
/// </summary>
/// <remarks>
/// The check that existed for this was a registration-time throw guarded on
/// <c>DataProtection:BlobUri</c> / <c>Storage:ConnectionString</c> — the two settings that make
/// <c>AddAuthagonal</c> attach the Azure repository itself. A SQL or S3 host attaches its own
/// repository through its provider package, which that guard cannot see, so every non-Azure persistent
/// backend walked straight past a check written specifically to stop this. These tests pin the
/// behaviour to the resolved <see cref="KeyManagementOptions"/> instead, which is the only place the
/// answer is true for all of them, and pin the refuse-versus-warn line that keeps an upgrade of a
/// running deployment from becoming an outage.
/// </remarks>
public sealed class KeyRingStartupCheckTests
{
    // ── harness ──────────────────────────────────────────────────────────────────

    private sealed class Capture : ILogger<Authagonal.Server.Services.KeyRingStartupCheck>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));

        public bool HasCritical => Entries.Any(e => e.Level == LogLevel.Critical);
        public string CriticalText => string.Join("\n", Entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message));
    }

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Authagonal.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>
    /// Stands in for the Azure blob and S3 repositories, which need a live account to construct. What
    /// the check reads from any of them is identical — a non-null <see cref="IXmlRepository"/> and what
    /// it returns from <see cref="GetAllElements"/> — and treating every backend the same is the point
    /// of the fix, so the substitution is faithful to the behaviour under test.
    /// </summary>
    private sealed class StubRepository(params string[] existingKeys) : IXmlRepository
    {
        public bool ThrowOnRead { get; init; }

        public IReadOnlyCollection<XElement> GetAllElements()
            => ThrowOnRead
                ? throw new InvalidOperationException("store unreachable")
                : [.. existingKeys.Select(k => new XElement("key", k))];

        public void StoreElement(XElement element, string friendlyName) { }
    }

    /// <summary>Runs the real check over real resolved options, and reports what it did.</summary>
    private static (Exception? Refusal, Capture Log) Run(
        IServiceCollection services, bool allowUnencrypted = false, string environment = "Production")
    {
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<KeyManagementOptions>>();
        var log = new Capture();

        IHostedService check = new Authagonal.Server.Services.KeyRingStartupCheck(
            options, new FakeEnvironment(environment), log, allowUnencrypted);

        try
        {
            check.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return (null, log);
        }
        catch (Exception ex)
        {
            return (ex, log);
        }
    }

    private static ServiceCollection SqlPersisted(SqlDataSource? source = null)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.PersistDataProtectionKeysToSql(source ?? SqlTestSource.Sqlite());
        return services;
    }

    // ── the gap this exists to close ─────────────────────────────────────────────

    /// <summary>
    /// The case that was silent: a SQL host, no DataProtection key configured. Nothing has been
    /// created yet, so refusing costs nothing and stops the insecure ring ever existing.
    /// </summary>
    [Fact]
    public void SqlPersisted_Unencrypted_NewRing_RefusesToStart()
    {
        var (refusal, _) = Run(SqlPersisted());

        Assert.NotNull(refusal);
        Assert.Contains("persisted but not encrypted", refusal!.Message);
        Assert.Contains("DataProtection:KeyVaultKeyId", refusal.Message);
        Assert.Contains("AllowUnencryptedKeyRing", refusal.Message);
    }

    /// <summary>
    /// The same misconfiguration on a deployment that is already running. Its users' cookies are
    /// encrypted under the keys already in that ring, so refusing to boot on a version bump would be
    /// an outage — it warns instead, at Critical, naming the remedy.
    /// </summary>
    [Fact]
    public void SqlPersisted_Unencrypted_ExistingRing_WarnsAndStarts()
    {
        var source = SqlTestSource.Sqlite();
        var services = SqlPersisted(source);

        // Put a key in the ring, exactly as a previously-running deployment would have.
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value
            .XmlRepository!.StoreElement(new XElement("key", "existing"), "k1");

        var (refusal, log) = Run(services);

        Assert.Null(refusal);
        Assert.True(log.HasCritical);
        Assert.Contains("already has keys", log.CriticalText);
        Assert.Contains("DataProtection:CertificateThumbprint", log.CriticalText);
    }

    /// <summary>The healthy case: durable and encrypted. It must say nothing at all.</summary>
    [Fact]
    public void SqlPersisted_Encrypted_StartsSilently()
    {
        using var cert = Authagonal.Server.Services.Saml.SamlSpKey.Load(
            Authagonal.Server.Services.Saml.SamlSpKey.CreateCertificate("https://keyring.test"));

        var services = new ServiceCollection();
        services.AddDataProtection().ProtectKeysWithCertificate(cert);
        services.PersistDataProtectionKeysToSql(SqlTestSource.Sqlite());

        var (refusal, log) = Run(services);

        Assert.Null(refusal);
        Assert.Empty(log.Entries);
    }

    // ── the backends that were already covered stay covered ──────────────────────

    /// <summary>
    /// Azure and S3 reach the check by the same route every other backend does — a repository on the
    /// resolved options. The registration-time guard they used to rely on is gone, so this pins that
    /// they did not lose the refusal in the move.
    /// </summary>
    [Fact]
    public void OtherBackendRepository_Unencrypted_NewRing_StillRefuses()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new StubRepository());

        var (refusal, _) = Run(services);

        Assert.NotNull(refusal);
        Assert.Contains("persisted but not encrypted", refusal!.Message);
    }

    [Fact]
    public void OtherBackendRepository_Unencrypted_ExistingRing_WarnsAndStarts()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new StubRepository("key-from-last-release"));

        var (refusal, log) = Run(services);

        Assert.Null(refusal);
        Assert.True(log.HasCritical);
    }

    // ── the other two outcomes, which used to be separate classes ────────────────

    /// <summary>
    /// No repository at all. Not a confidentiality problem — an availability one — so it warns and
    /// starts, and it must not be mistaken for the unencrypted case.
    /// </summary>
    [Fact]
    public void NoRepository_WarnsThatTheRingIsEphemeral_AndStarts()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();

        var (refusal, log) = Run(services);

        Assert.Null(refusal);
        Assert.True(log.HasCritical);
        Assert.Contains("does not survive a", log.CriticalText);
        Assert.DoesNotContain("persisted but NOT encrypted", log.CriticalText);
    }

    /// <summary>Explicitly acknowledged plaintext: allowed, but restated at Critical every start.</summary>
    [Fact]
    public void AcknowledgedPlaintextRing_WarnsAndStarts()
    {
        var (refusal, log) = Run(SqlPersisted(), allowUnencrypted: true);

        Assert.Null(refusal);
        Assert.True(log.HasCritical);
        Assert.Contains("AllowUnencryptedKeyRing is set", log.CriticalText);
    }

    // ── the edges ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Development runs on SQLite with no key as a matter of course — the quick start does exactly
    /// that — so refusing there would break `docker compose up` for everyone.
    /// </summary>
    [Fact]
    public void Development_DoesNotRefuse_OnAnUnencryptedNewRing()
    {
        var (refusal, log) = Run(SqlPersisted(), environment: "Development");

        Assert.Null(refusal);
        Assert.False(log.HasCritical);
    }

    /// <summary>
    /// If the store cannot be read, new and existing are indistinguishable. Refusing on the strength
    /// of a failed read would turn a transient blip into an outage, so it warns.
    /// </summary>
    [Fact]
    public void UnreadableRepository_WarnsRatherThanRefusing()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new StubRepository { ThrowOnRead = true });

        var (refusal, log) = Run(services);

        Assert.Null(refusal);
        Assert.True(log.HasCritical);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("new or an"));
    }
}
