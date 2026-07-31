using System.Net;
using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Assertion checks that used to fail open, and the binding between a verified signature and the
/// party the connection is actually configured for.
/// </summary>
public sealed class SamlValidationHardeningTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F93 / F260 / F337 — time bounds must fail closed
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("2026-07-31T12:00:00Z", true)]
    [InlineData("2026-07-31T12:00:00.000Z", true)]
    [InlineData("2026-07-31T12:00:00+01:00", true)]
    [InlineData("not-a-timestamp", false)]
    [InlineData("", false)]
    [InlineData("31/07/2026 12:00", false)]
    public void SamlInstant_ParsesOnlyRealXmlTimestamps(string value, bool expected)
    {
        // A value that will not parse used to skip the comparison entirely, so a malformed NotOnOrAfter
        // removed the assertion's validity window rather than failing validation — the assertion became
        // acceptable forever.
        Assert.Equal(expected, SamlResponseParser.TryParseSamlInstant(value, out _));
    }

    [Fact]
    public void SamlInstant_IsParsedCultureIndependently()
    {
        // TryParse without an explicit culture honours the ambient one, so the same assertion could
        // parse differently on two pods with different locale settings.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.True(SamlResponseParser.TryParseSamlInstant("2026-07-31T12:00:00Z", out var parsed));
            Assert.Equal(2026, parsed.UtcDateTime.Year);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // -----------------------------------------------------------------------
    // F63 — a verified signature must come from the configured IdP
    // -----------------------------------------------------------------------
    //
    // Covered by SamlVendorQuirkTests.F56_ReturnUrl_RidesServerSide_NotRelayState rather than a
    // test here: that fixture configured Okta's metadata while signing an assertion issued as
    // https://idp.test, and the ACS now rejects it — which is exactly the mismatch this check
    // exists to catch. Aligning the fixture's issuer with the metadata it configures is what makes
    // it pass again, so the test pins the binding from the other direction.
}
