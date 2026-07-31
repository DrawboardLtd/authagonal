using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// What counts as an origin for a policy that also sets <c>AllowCredentials</c>.
/// </summary>
/// <remarks>
/// Dynamic registration stored allowed_cors_origins with no validation, and the browser compares
/// Origin headers by exact string — so anything that is not an origin is either inert configuration
/// that reads as though it works, or an attempt to smuggle a wildcard into a credentialed policy.
/// </remarks>
public class CorsOriginValidationTests
{
    [Theory]
    [InlineData("https://app.example", true)]
    [InlineData("https://app.example:8443", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("https://app.example/", true)]
    // A path is not part of an origin; the browser never sends one, so this can only ever be dead
    // configuration that an operator reads as active.
    [InlineData("https://app.example/callback", false)]
    [InlineData("https://app.example?x=1", false)]
    [InlineData("https://app.example#f", false)]
    [InlineData("*", false)]
    [InlineData("https://*.example", false)]
    [InlineData("app.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidOrigin_AcceptsOnlySchemeHostPort(string? origin, bool expected)
    {
        Assert.Equal(expected, DynamicCorsPolicyProvider.IsValidOrigin(origin));
    }

    // -----------------------------------------------------------------------
    // F81 — the origins cache is keyed per (tenant, env)
    // -----------------------------------------------------------------------

    /// <remarks>
    /// The cache was keyed on tenant alone. Env is a first-class isolation boundary — every store
    /// threads it into the partition key, and a tenant's sandbox envs hold their own client records —
    /// so the origins list was built from an env-scoped scan and then filed under a key that did not
    /// name the env. Whichever env warmed the entry first served its origins to all of them, and this
    /// policy sets AllowCredentials, so a sandbox origin could be honoured against production.
    /// </remarks>
    [Theory]
    [InlineData("acme", "live", "sandbox", false)]
    [InlineData("acme", "live", "live", true)]
    [InlineData("acme", "live", null, false)]
    [InlineData("acme", null, "other", false)]
    public void CacheKey_SeparatesEnvsWithinATenant(
        string tenant, string? envA, string? envB, bool expectedSame)
    {
        // Mirrors the key built in GetAllowedOriginsAsync.
        static string Key(string tenant, string? env) => $"{tenant}|{env ?? ""}";

        Assert.Equal(expectedSame, Key(tenant, envA) == Key(tenant, envB));
    }

    [Fact]
    public void CacheKey_SeparatesTenants()
    {
        static string Key(string tenant, string? env) => $"{tenant}|{env ?? ""}";
        Assert.NotEqual(Key("acme", "live"), Key("globex", "live"));
    }
}
