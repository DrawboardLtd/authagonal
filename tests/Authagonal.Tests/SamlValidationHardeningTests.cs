using System.Net;
using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

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
    // F260 / F285 — the remaining fail-open gates in the assertion pipeline
    // -----------------------------------------------------------------------

    private const string Acs = "https://sp.test/saml/c1/acs";
    private const string Audience = "https://sp.test/saml/c1";

    private static SamlParseResult ParseWithDefaults(string base64Response) =>
        new SamlResponseParser(NullLogger<SamlResponseParser>.Instance).Parse(
            base64Response,
            new SamlResponseValidationContext(Acs, Audience, null, [SamlTestHelper.TestCertificate]));

    /// <summary>
    /// F260 — Destination is only optional on an UNSIGNED message. Core §3.2.2 makes it mandatory once
    /// the message is signed, and it is the SP's only evidence the Response was addressed here: without
    /// it, one signed Response is valid at every SP in the federation that trusts the signer, so it can
    /// be forwarded from the SP it was meant for to this one.
    /// </summary>
    [Fact]
    public void SignedResponse_WithNoDestination_IsRefused()
    {
        var result = ParseWithDefaults(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com", includeDestination: false));

        Assert.False(result.Success);
        Assert.Contains("Destination", result.Error);
    }

    /// <summary>Control: the same document with its Destination is accepted, so the refusal above is the Destination.</summary>
    [Fact]
    public void SignedResponse_WithDestination_IsAccepted()
    {
        var result = ParseWithDefaults(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com"));

        Assert.True(result.Success, result.Error);
    }

    /// <summary>
    /// F260 — Core §2.5.1: a condition the SP cannot evaluate makes the assertion Invalid, not
    /// unconditioned. Only AudienceRestriction was ever selected, so anything else the IdP attached was
    /// silently dropped and the IdP believed it had constrained an assertion this SP accepted freely.
    /// </summary>
    [Fact]
    public void Assertion_WithAnUnevaluableCondition_IsRefused()
    {
        var result = ParseWithDefaults(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            extraConditionsXml: @"<saml:Condition xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xsi:type=""x:DeviceBoundCondition"" xmlns:x=""urn:example:conditions"" />"));

        Assert.False(result.Success);
        Assert.Contains("condition", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The conditions this SP does satisfy stay acceptable: OneTimeUse, because every assertion ID goes
    /// through the single-use replay cache before the ACS acts on it, and ProxyRestriction, because this
    /// SP never re-issues assertions derived from one it consumed.
    /// </summary>
    [Fact]
    public void Assertion_WithOneTimeUseAndProxyRestriction_IsAccepted()
    {
        var result = ParseWithDefaults(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            extraConditionsXml: @"<saml:OneTimeUse /><saml:ProxyRestriction Count=""0"" />"));

        Assert.True(result.Success, result.Error);
    }

    /// <summary>
    /// F285 — Web Browser SSO §4.1.4.2 requires at least one AuthnStatement. It was read with `?.`
    /// throughout, so an assertion carrying none parsed successfully with a null SessionIndex and a null
    /// SessionNotOnOrAfter — meaning the one shape of assertion that opted out of BOTH session controls
    /// (the SLO binding and the IdP's session bound) was the one that never claimed an authentication
    /// had happened.
    /// </summary>
    [Fact]
    public void Assertion_WithNoAuthnStatement_IsRefused()
    {
        var result = ParseWithDefaults(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com", includeAuthnStatement: false));

        Assert.False(result.Success);
        Assert.Contains("AuthnStatement", result.Error);
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
