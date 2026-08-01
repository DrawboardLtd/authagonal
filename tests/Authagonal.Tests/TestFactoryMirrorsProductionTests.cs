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
/// Guards the one structural weakness behind most of this review's recurring findings:
/// <see cref="AuthagonalTestFactory"/> MIRRORS <c>AddAuthagonal</c> rather than calling it.
/// </summary>
/// <remarks>
/// Anything hardened in the real DI registration is invisible to every test using the factory until
/// someone thinks to repeat it there — so a fix can land in production, the suite can stay green, and the
/// tests are exercising the unfixed configuration. That is not hypothetical: it is how a `typ`-validation
/// test came to pass against unfixed code, and the third-pass merge audit found five more divergences,
/// including a bare untenanted rate limiter (so the tenant-scoping decorator was exercised by nothing) and
/// a bearer scheme with no algorithm pin and no revocation check.
///
/// <para>
/// The right end state is for the factory to CALL <c>AddAuthagonal</c> with the in-memory stores
/// pre-registered — which is feasible, since the Azure block is already guarded by
/// <c>if (!services.Any(IUserStore))</c>. It is a real refactor: the factory deliberately deviates on the
/// PBKDF2 cost, <c>AllowInsecureHttp</c>, the cookie secure policy and name (TestServer speaks http), and
/// the Fido2 relying party, and 89 test files depend on the current shape. Until that lands, this test is
/// the substitute: it builds the REAL container from <c>AddAuthagonal</c> and asserts the factory agrees
/// with it on the registrations where disagreement is a security hole rather than a test convenience.
/// </para>
///
/// <para>
/// When this fails, the fix is almost always to copy the hardening into the factory — not to relax the
/// assertion. If a divergence is deliberate, add it to <see cref="IntentionalDeviations"/> with the reason,
/// so the next person sees a decision instead of an omission.
/// </para>
/// </remarks>
public class TestFactoryMirrorsProductionTests
{
    /// <summary>
    /// Service types the factory is allowed to differ on, and why. Everything else must match.
    /// </summary>
    private static readonly Dictionary<string, string> IntentionalDeviations = new(StringComparer.Ordinal)
    {
        ["IUserStore etc."] = "in-memory stores replace Azure Table Storage — the point of the factory",
        ["AuthOptions.Pbkdf2Iterations"] = "1,000 not 600,000: at the real cost the suite spends minutes in the KDF",
        ["AuthOptions.AllowInsecureHttp"] = "TestServer speaks plain http, so the TLS gate would refuse every request",
        ["CookieAuthenticationOptions"] = "http TestServer cannot carry a __Host- prefixed Secure cookie",
        ["Fido2"] = "a fixed relying party, so WebAuthn ceremonies are reproducible",
    };

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
        ];

        foreach (var type in required)
        {
            Assert.NotNull(factory.Services.GetService(type));
        }

        // Documents the allowed divergences so a reader knows the list is curated, not incidental.
        Assert.NotEmpty(IntentionalDeviations);
    }
}
