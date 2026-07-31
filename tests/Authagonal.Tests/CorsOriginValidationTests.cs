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
}
