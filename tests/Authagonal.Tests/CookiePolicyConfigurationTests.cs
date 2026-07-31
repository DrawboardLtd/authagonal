using Authagonal.Server;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// The session cookie's own attributes, asserted against the PRODUCTION wiring.
//
// AuthagonalTestFactory deliberately does not call AddAuthagonal — it mirrors it — and it overrides
// SecurePolicy to SameAsRequest because TestServer speaks HTTP and CookieContainer will not send a
// Secure cookie over it. The consequence was that Secure/__Host- were production-path only and no
// test touched them: the factory could drift from the thing it mirrors and everything stayed green.
//
// These tests sidestep that entirely by never making a request. They build the real container, run
// the real AddAuthagonalCore, and read the resolved CookieAuthenticationOptions. There is no HTTP
// involved, so TestServer's transport limitation simply does not apply.
// -------------------------------------------------------------------------------------------------
public sealed class CookiePolicyConfigurationTests
{
    private static CookieAuthenticationOptions ResolveCookieOptions(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        // AddCookie's own post-configuration builds a TicketDataFormat and needs a provider. In the
        // real host this comes from AddAuthagonal's storage section, which is not exercised here.
        services.AddDataProtection();
        services.AddAuthagonalCore(configuration);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// The default posture. SameAsRequest looks equivalent behind a TLS-terminating proxy but depends
    /// on X-Forwarded-Proto arriving and being trusted; when it doesn't, the session cookie is issued
    /// without Secure and then rides plaintext requests to the same host, silently.
    /// </summary>
    [Fact]
    public void Default_SessionCookie_IsSecureAndHostPrefixed()
    {
        var options = ResolveCookieOptions();

        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.StartsWith("__Host-", options.Cookie.Name);
        // __Host- is only honoured by the browser alongside these two; a prefix without them is
        // rejected outright and the session cookie silently stops being set.
        Assert.Equal("/", options.Cookie.Path);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
    }

    /// <summary>
    /// The documented escape hatch for hosts genuinely served over HTTP (local development). The
    /// prefix has to come off with it: the browser rejects a __Host- cookie that is not Secure, so
    /// leaving the prefix on would drop the session cookie entirely rather than merely weaken it.
    /// </summary>
    [Fact]
    public void AllowInsecureCookie_RelaxesPolicyAndDropsThePrefix()
    {
        var options = ResolveCookieOptions(("Authentication:AllowInsecureCookie", "true"));

        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
        Assert.DoesNotContain("__Host-", options.Cookie.Name);
    }

    /// <summary>
    /// __Host- forbids a Domain attribute, so a host that asks for a cookie domain cannot have that
    /// prefix — but it must still get the domain it asked for, and the strongest prefix that remains.
    /// The setting was previously read only to suppress __Host- and never applied, which cost origin
    /// binding and returned nothing.
    /// </summary>
    [Fact]
    public void CookieDomain_IsApplied_AndFallsBackToSecurePrefix()
    {
        var options = ResolveCookieOptions(("Authentication:CookieDomain", "login.example.com"));

        Assert.Equal("login.example.com", options.Cookie.Domain);
        Assert.StartsWith("__Secure-", options.Cookie.Name);
        Assert.DoesNotContain("__Host-", options.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }

    /// <summary>
    /// Both opt-outs together: the domain still applies, but no prefix survives, since every cookie
    /// prefix requires Secure and the browser would reject the cookie outright without it.
    /// </summary>
    [Fact]
    public void CookieDomain_WithInsecureCookies_AppliesDomainAndNoPrefix()
    {
        var options = ResolveCookieOptions(
            ("Authentication:CookieDomain", "login.example.com"),
            ("Authentication:AllowInsecureCookie", "true"));

        Assert.Equal("login.example.com", options.Cookie.Domain);
        Assert.DoesNotContain("__Host-", options.Cookie.Name);
        Assert.DoesNotContain("__Secure-", options.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
    }
}
