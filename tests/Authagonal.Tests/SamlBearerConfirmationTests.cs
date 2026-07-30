using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// SAML 2.0 Profiles §4.1.4.2/§4.1.4.3 (with errata E52/E26) require at least one bearer
/// <c>SubjectConfirmation</c> whose <c>SubjectConfirmationData</c> carries a <c>Recipient</c> matching this
/// ACS and a <c>NotOnOrAfter</c> bounding confirmation, and require the SP to verify each. Every one of
/// those checks used to run only "if present", so an assertion omitting any of them was accepted.
///
/// This is not merely a conformance gap. <c>SubjectConfirmationData/NotOnOrAfter</c> is the SHORT bound —
/// minutes at Entra, Okta and Google — while <c>Conditions/NotOnOrAfter</c> is the long one, around an hour.
/// With the short bound absent, an assertion stayed acceptable far longer than its issuer intended, long
/// enough to outlive the replay cache's retention and be presented again as a first sighting. It is the
/// other half of the replay finding, which is why both were fixed together.
/// </summary>
public sealed class SamlBearerConfirmationTests
{
    private const string Acs = "https://sp.test/saml/c1/acs";
    private const string Audience = "https://sp.test/saml/c1";

    private static SamlParseResult Parse(string base64Response) =>
        new SamlResponseParser(NullLogger<SamlResponseParser>.Instance).Parse(
            base64Response,
            new SamlResponseValidationContext(Acs, Audience, null, [SamlTestHelper.TestCertificate]));

    /// <summary>A conforming assertion still works — the checks must not break real IdPs.</summary>
    [Fact]
    public void Conforming_bearer_confirmation_is_accepted()
    {
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("user@example.com", result.NameId);
        // And the acceptability deadline is surfaced, so the replay cache can retain the id long enough.
        Assert.NotNull(result.AcceptableUntil);
        Assert.True(result.AcceptableUntil > DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Each non-conforming shape must be refused. Previously all five were accepted.
    /// </summary>
    [Theory]
    [InlineData(SubjectConfirmationShape.Absent)]
    [InlineData(SubjectConfirmationShape.NoConfirmationData)]
    [InlineData(SubjectConfirmationShape.NoRecipient)]
    [InlineData(SubjectConfirmationShape.NoNotOnOrAfter)]
    [InlineData(SubjectConfirmationShape.UnparseableNotOnOrAfter)]
    public void Non_conforming_bearer_confirmation_is_refused(SubjectConfirmationShape shape)
    {
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com", confirmationShape: shape));

        Assert.False(result.Success,
            $"shape {shape} was accepted; the bearer confirmation checks are fail-open again");
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// An expired confirmation window is refused even while Conditions/NotOnOrAfter is still open — the
    /// short bound is the one that governs, and honouring it is what keeps the replay window closed.
    /// </summary>
    [Fact]
    public void Expired_confirmation_window_is_refused()
    {
        // validFor is negative, so both NotOnOrAfter values are already in the past.
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            validFor: TimeSpan.FromMinutes(-30)));

        Assert.False(result.Success);
    }

    /// <summary>
    /// The Recipient must match THIS ACS: an assertion minted for a different endpoint must not be
    /// replayable here.
    /// </summary>
    [Fact]
    public void Recipient_for_another_acs_is_refused()
    {
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            "https://other.test/saml/x/acs", Audience, "user@example.com", email: "user@example.com"));

        Assert.False(result.Success);
    }
}
