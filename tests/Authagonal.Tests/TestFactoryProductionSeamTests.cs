using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// Guards the boundary between <see cref="AuthagonalTestFactory"/> and the real registration, now that the
/// factory CALLS it rather than restating it.
/// </summary>
/// <remarks>
/// This class was written as a stopgap: the factory mirrored <c>AddAuthagonal</c>, so anything hardened in
/// the real DI registration was invisible to every test using it until someone repeated it there, and these
/// assertions were how that drift got detected instead of shipped. The factory now calls
/// <c>AddAuthagonalCore</c> with the stores and doubles pre-registered, so the drift-generating machinery is
/// gone and most of what this class asserted is true by construction — changing <c>ValidAlgorithms</c> in
/// <c>AddAuthagonalCore</c> fails 168 tests, where it used to fail only the comparison below.
/// <para>
/// What is left to guard is the SEAM. Two things can still diverge and neither is structural:
/// </para>
/// <list type="number">
/// <item>
/// The factory calls <c>AddAuthagonalCore</c>, not <c>AddAuthagonal</c>. The difference is storage
/// (replaced), the DataProtection key ring (no store here), and the background/seed hosted services (a test
/// host wants no timers) — plus four registrations the factory restates. If something security-relevant is
/// ever added to <c>AddAuthagonal</c> OUTSIDE <c>AddAuthagonalCore</c>, no test host sees it, and the
/// resolvability check below is what notices.
/// </item>
/// <item>
/// The deliberate deviations in <see cref="IntentionalDeviations"/>. Each is something a test host cannot
/// have; the list is short, and short on purpose. Adding an entry is a deliberate act that must state why.
/// </item>
/// </list>
/// <para>
/// When this fails, the fix is almost always to move a registration ABOVE the <c>AddAuthagonalCore</c> call
/// in the factory — not to relax the assertion.
/// </para>
/// </remarks>
public class TestFactoryProductionSeamTests
{
    /// <summary>
    /// Everything the factory is allowed to differ on, and why. Everything else must match.
    /// </summary>
    /// <remarks>
    /// Three of the five entries the mirrored factory needed are gone. The cookie policy and the TLS gate
    /// are plain CONFIGURATION on the real registration (<c>Authentication:AllowInsecureCookie</c>,
    /// <c>Auth:AllowInsecureHttp</c>), so the test host sets what an operator sets and shares the code. And
    /// the Fido2 relying party was never a production registration at all — <c>WebAuthnService</c> resolves
    /// it per request from the request host, because a single startup value cannot be right on a
    /// multi-tenant server — so the factory's <c>AddFido2</c> and its singleton <c>WebAuthnService</c> were
    /// themselves the divergence. Deleted rather than documented.
    /// </remarks>
    private static readonly Dictionary<string, string> IntentionalDeviations = new(StringComparer.Ordinal)
    {
        ["IUserStore etc."] = "in-memory stores replace Azure Table Storage — the point of the factory, and "
            + "what makes AddAuthagonal's storage block skip itself",
        ["AuthOptions.Pbkdf2Iterations"] = "1,000 not 600,000, with the startup floor validator removed so "
            + "the host will start below it: at any conforming cost the suite spends minutes in the KDF and "
            + "measures nothing but the KDF",
        ["AddAuthagonalCore, not AddAuthagonal"] = "no storage, no DataProtection key ring, no background or "
            + "seed hosted services. Every divergence that has actually caused a miss lives in Core",
        ["Named outbound HttpClient primary handlers"] = "test handlers replace the primary handler only, so "
            + "the production timeout and redirect policy for each name still apply",
    };

    /// <summary>
    /// What <c>AddAuthagonal</c> registers that <c>AddAuthagonalCore</c> does not — the exact gap the test
    /// host does not cover — must stay a known, reviewed list.
    /// </summary>
    /// <remarks>
    /// The factory calls Core because what sits above it is storage, the DataProtection key ring and a set of
    /// background timers, none of which belong in a test host. The cost of that choice is precisely this set:
    /// anything registered here is exercised by no test using the factory. Today it is storage-shaped and
    /// hosted-service-shaped, which is why the choice is safe. This test fails when something NEW appears in
    /// the gap, so the decision gets re-made deliberately rather than inherited.
    /// <para>
    /// Compared as descriptor sets, without building a provider: the question is what is registered, and
    /// resolving would drag in Azure credentials for services this host never uses.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGapBetweenCoreAndFullRegistration_IsOnlyStorageAndBackgroundWork()
    {
        static HashSet<string> Registered(bool full)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Issuer"] = AuthagonalTestFactory.TestIssuer,
                ["Cluster:Enabled"] = "false",
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IUserStore>(new InMemoryUserStore());
            services.AddSingleton<IClientStore>(new InMemoryClientStore());
            services.AddSingleton<IGrantStore>(new InMemoryGrantStore());
            services.AddSingleton<ISigningKeyStore>(new InMemorySigningKeyStore());

            if (full) services.AddAuthagonal(configuration);
            else services.AddAuthagonalCore(configuration);

            return [.. services.Select(d => d.ServiceType.FullName ?? d.ServiceType.Name)];
        }

        // What may legitimately sit in the gap, matched by PREFIX — an assembly-qualified generic type
        // name embeds a version, and pinning those would turn a runtime bump into a failure about nothing.
        //
        // Every entry is storage-shaped or background-shaped, which is exactly why calling Core is safe:
        //   - IHostedService covers every background and seed service in one line. That is the right
        //     granularity; they are all "work a test host does not want running", and naming each would
        //     make a list nobody maintains.
        //   - the JwtBearer key resolver and the storage health check ARE restated in the factory's
        //     deviations section, so they are covered — they appear here only because Core does not add them.
        string[] expectedGap =
        [
            "Microsoft.Extensions.Hosting.IHostedService",
            "Microsoft.Extensions.Options.IPostConfigureOptions`1[[Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions",
            "Authagonal.Server.Services.TableStorageHealthCheck",
            "Microsoft.Extensions.Diagnostics.HealthChecks.",
            "Microsoft.Extensions.Options.IConfigureOptions`1[[Microsoft.Extensions.Diagnostics.HealthChecks.",
        ];

        var gap = Registered(full: true);
        gap.ExceptWith(Registered(full: false));

        var unexpected = gap
            .Where(t => !expectedGap.Any(e => t.StartsWith(e, StringComparison.Ordinal)))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(unexpected.Count == 0,
            "AddAuthagonal now registers something outside AddAuthagonalCore, and AuthagonalTestFactory calls "
            + "Core — so no test using the factory exercises it. Either move it into Core, or restate it in "
            + "the factory's deviations section and add it here with a reason. New in the gap:"
            + Environment.NewLine + string.Join(Environment.NewLine, unexpected));
    }

    /// <summary>The real container, built the way a host builds it.</summary>
    private static ServiceProvider ProductionContainer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Issuer"] = AuthagonalTestFactory.TestIssuer,
            ["Oidc:Issuer"] = AuthagonalTestFactory.TestIssuer,
            ["AdminApi:Enabled"] = "true",
            ["Cluster:Enabled"] = "false",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // Pre-registering the stores is what makes AddAuthagonal skip its Azure block, so this needs no
        // storage account and no credential — the same seam the factory itself relies on.
        services.AddSingleton<IUserStore>(new InMemoryUserStore());
        services.AddSingleton<IClientStore>(new InMemoryClientStore());
        services.AddSingleton<IGrantStore>(new InMemoryGrantStore());
        services.AddSingleton<ISigningKeyStore>(new InMemorySigningKeyStore());
        services.AddAuthagonal(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The resource-server bearer scheme must be configured identically: an unpinned algorithm or a
    /// missing revocation check in the test host means no test can observe either being wrong.
    /// </summary>
    [Fact]
    public async Task TheBearerSchemeMatchesProduction()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        var testOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        using var production = ProductionContainer();
        var realOptions = production
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(
            realOptions.TokenValidationParameters.ValidAlgorithms,
            testOptions.TokenValidationParameters.ValidAlgorithms);
        Assert.Equal(
            realOptions.TokenValidationParameters.ValidTypes,
            testOptions.TokenValidationParameters.ValidTypes);
        Assert.Equal(
            realOptions.TokenValidationParameters.ValidateIssuerSigningKey,
            testOptions.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.Equal(
            realOptions.TokenValidationParameters.ValidateLifetime,
            testOptions.TokenValidationParameters.ValidateLifetime);

        // The revocation check lives in OnTokenValidated. Its presence is the assertion — a scheme with no
        // events cannot reject a revoked-but-valid token, and nothing else in the pipeline does it.
        Assert.NotNull(realOptions.Events?.OnTokenValidated);
        Assert.NotNull(testOptions.Events?.OnTokenValidated);
    }

    /// <summary>
    /// The rate limiter must reach the tenant-scoping decorator in both containers. Registering the bare
    /// in-process limiter here meant every key was counted WITHOUT its tenant prefix, so the cross-tenant
    /// budget exhaustion the decorator prevents was unobservable from the suite.
    /// </summary>
    [Fact]
    public async Task TheRateLimiterIsTenantScopedInBothContainers()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        using var production = ProductionContainer();

        Assert.IsType<TenantScopedRateLimiter>(production.GetRequiredService<IRateLimiter>());
        Assert.IsType<TenantScopedRateLimiter>(factory.Services.GetRequiredService<IRateLimiter>());
    }

    /// <summary>
    /// Every named outbound HttpClient production registers must be registered here too, and must refuse
    /// redirects by default.
    /// </summary>
    /// <remarks>
    /// Two failure modes in one assertion. A name production registers and the factory does not means
    /// <c>CreateClient</c> in a test silently returns a redirect-following default, so the SSRF guards that
    /// depend on redirects being refused pass for the wrong reason. And a name registered bare here while
    /// production hardens it means the same thing more quietly.
    /// </remarks>
    [Theory]
    [InlineData("Provisioning")]
    [InlineData("SamlMetadata")]
    [InlineData("OidcDiscovery")]
    [InlineData("AuthagonalJwks")]
    [InlineData("BackChannelLogout")]
    [InlineData("Resend")]
    public async Task EveryNamedOutboundClientIsRegisteredWithABoundedTimeout(string name)
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        using var production = ProductionContainer();

        var realClient = production.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        var testClient = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        // An unregistered name yields the framework default of 100 seconds, which is the tell.
        Assert.True(realClient.Timeout < TimeSpan.FromSeconds(100),
            $"production registers \"{name}\" without a bounded timeout");
        Assert.Equal(realClient.Timeout, testClient.Timeout);
    }

    /// <summary>
    /// A cheap catch-all: the security-critical singletons production registers must all resolve here too.
    /// </summary>
    /// <remarks>
    /// Deliberately a resolvability check rather than a full container diff. A diff over every descriptor
    /// would fail on every legitimate deviation and would be deleted within a month; this asks the narrower
    /// question that actually matters — is there a guard production installs that this host does not have at
    /// all.
    /// </remarks>
    [Fact]
    public async Task TheSecurityCriticalServicesAllResolve()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        Type[] required =
        [
            typeof(IRateLimiter),
            typeof(IClientSecretVerifier),
            typeof(IRevokedTokenStore),
            typeof(PasswordHasher),
            typeof(PasswordValidator),
            // Not a guard — the operator's escape from one. Its absence here is fail-CLOSED (every internal
            // outbound target refused), which is why it would go unnoticed: the suite would stay green while
            // no test could observe Auth:AllowedInternalTargets doing anything at all.
            typeof(OutboundAllowlist),
        ];

        foreach (var type in required)
        {
            Assert.NotNull(factory.Services.GetService(type));
        }

        // Documents the allowed divergences so a reader knows the list is curated, not incidental.
        Assert.NotEmpty(IntentionalDeviations);
    }
}
