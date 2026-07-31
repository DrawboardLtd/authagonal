using System.Security.Claims;
using Authagonal.Server;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// The 7-day absolute session cap, asserted against the PRODUCTION cookie wiring.
/// </summary>
/// <remarks>
/// The cap reads <c>session_started</c>, a property sliding renewal never touches — but only
/// CookieSignInHelper wrote it, and the two federated sign-ins bypass that helper: the OIDC callback
/// calls SignInAsync with no AuthenticationProperties at all, and the SAML ACS builds its own bag
/// carrying only ExpiresUtc/IsPersistent. Both read back as "no stamp", the guard was written
/// <c>is { } started &amp;&amp;</c>, and the branch was skipped — so every federated session, the ones a
/// stolen IdP cookie rides longest, had no absolute lifetime at all.
/// <para>
/// The stamp is now written by the scheme's own OnSigningIn event, which is the single funnel every
/// SignInAsync on this scheme passes through. AuthagonalTestFactory mirrors the cookie wiring rather
/// than calling it (TestServer speaks http and will not carry a Secure cookie), so these tests build the
/// real container and invoke the real events with no HTTP involved — the same approach
/// CookiePolicyConfigurationTests takes for the cookie attributes.
/// </para>
/// </remarks>
public sealed class AbsoluteSessionLifetimeTests
{
    private static CookieAuthenticationOptions ResolveCookieOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Issuer"] = "https://auth.test" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthagonalCore(configuration);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static AuthenticationScheme Scheme() => new(
        CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
        [new Claim("sub", "user-1"), new Claim("security_stamp", "stamp-1")],
        CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>
    /// The OIDC callback's shape: SignInAsync with no properties of its own. The handler supplies an
    /// empty bag, and the event has to be what puts the stamp in it.
    /// </summary>
    [Fact]
    public async Task SigningIn_withNoPropertiesOfItsOwn_stampsTheSessionStart()
    {
        var options = ResolveCookieOptions();
        var properties = new AuthenticationProperties();

        await options.Events.SigningIn(new CookieSigningInContext(
            new DefaultHttpContext(), Scheme(), options, Principal(), properties, new CookieOptions()));

        var started = CookieSignInHelper.SessionStartedAt(properties);
        Assert.NotNull(started);
        Assert.True(DateTimeOffset.UtcNow - started!.Value < TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The SAML ACS's shape: a properties bag the endpoint built for its own reasons. Those must
    /// survive, and the stamp must be added alongside them.
    /// </summary>
    [Fact]
    public async Task SigningIn_withTheSamlPropertiesBag_keepsItAndStampsTheSessionStart()
    {
        var options = ResolveCookieOptions();
        var idpBound = DateTimeOffset.UtcNow.AddHours(8);
        var properties = new AuthenticationProperties { ExpiresUtc = idpBound, IsPersistent = true };

        await options.Events.SigningIn(new CookieSigningInContext(
            new DefaultHttpContext(), Scheme(), options, Principal(), properties, new CookieOptions()));

        Assert.NotNull(CookieSignInHelper.SessionStartedAt(properties));
        // The bag serialises to second precision, so compare the instant rather than the ticks.
        Assert.True((properties.ExpiresUtc!.Value - idpBound).Duration() < TimeSpan.FromSeconds(2));
        Assert.True(properties.IsPersistent);
    }

    /// <summary>
    /// An existing stamp is never bumped. Re-stamping would slide the deadline forward on every
    /// sign-in-shaped event, which is the same defect as measuring against IssuedUtc.
    /// </summary>
    [Fact]
    public async Task SigningIn_doesNotBumpAnExistingStamp()
    {
        var options = ResolveCookieOptions();
        var original = DateTimeOffset.UtcNow.AddDays(-3);
        var properties = new AuthenticationProperties();
        properties.SetString(
            CookieSignInHelper.SessionStartedProperty, original.ToUnixTimeSeconds().ToString());

        await options.Events.SigningIn(new CookieSigningInContext(
            new DefaultHttpContext(), Scheme(), options, Principal(), properties, new CookieOptions()));

        Assert.Equal(original.ToUnixTimeSeconds(), CookieSignInHelper.SessionStartedAt(properties)!.Value.ToUnixTimeSeconds());
    }

    /// <summary>
    /// And the cap actually fires on a stamped session past 7 days — the whole point of stamping.
    /// </summary>
    [Fact]
    public async Task ValidatePrincipal_rejectsASessionOlderThanTheCap()
    {
        var options = ResolveCookieOptions();
        var properties = new AuthenticationProperties();
        properties.SetString(
            CookieSignInHelper.SessionStartedProperty,
            DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeSeconds().ToString());

        var context = ValidationContext(options, properties);
        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task ValidatePrincipal_leavesASessionInsideTheCapAlone()
    {
        var options = ResolveCookieOptions();
        var properties = new AuthenticationProperties();
        properties.SetString(
            CookieSignInHelper.SessionStartedProperty,
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds().ToString());

        var context = ValidationContext(options, properties);
        await options.Events.ValidatePrincipal(context);

        // Not rejected by the cap. (The stamp revalidation below it needs an IUserStore, which this
        // container has none of, so the run stops there — after the branch under test.)
        Assert.NotNull(context.Principal);
    }

    /// <summary>
    /// A session established before the stamp existed is adopted on first sight rather than exempted
    /// forever — the deadline starts running now, which bounds it without signing everyone out.
    /// </summary>
    [Fact]
    public async Task ValidatePrincipal_adoptsALegacySessionWithNoStamp()
    {
        var options = ResolveCookieOptions();
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddDays(-90) };

        var context = ValidationContext(options, properties);
        await options.Events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.NotNull(CookieSignInHelper.SessionStartedAt(context.Properties));
        Assert.True(context.ShouldRenew, "the adopted stamp must be persisted back to the cookie");
    }

    /// <summary>
    /// Enough of a container for the branch under test: AuthOptions for the revalidation window, and a
    /// real authentication stack because rejecting the principal signs the session out.
    /// </summary>
    private static ServiceProvider RequestServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddOptions();
        services.Configure<AuthOptions>(_ => { });
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        return services.BuildServiceProvider();
    }

    private static CookieValidatePrincipalContext ValidationContext(
        CookieAuthenticationOptions options, AuthenticationProperties properties)
    {
        // The security-stamp revalidation below the cap resolves AuthOptions and then an IUserStore.
        // stamp_validated inside the revalidation window short-circuits it, so these tests exercise the
        // absolute-lifetime branch without standing up a store.
        properties.SetString("stamp_validated", DateTimeOffset.UtcNow.ToString("O"));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = RequestServices(),
        };
        return new CookieValidatePrincipalContext(
            httpContext, Scheme(), options,
            new AuthenticationTicket(
                Principal(), properties, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
