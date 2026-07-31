using System.Text;
using System.Xml;
using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// The two halves of F6 that outlived the first fix: <c>Response@Destination</c> was compared only when
/// it happened to be present, and the assertion's own <c>IssueInstant</c> was never read at all, so
/// nothing this SP controlled bounded how old an assertion could be.
/// </summary>
/// <remarks>
/// Destination sits on the Response, outside the assertion. On the common IdP configuration — assertion
/// signed, Response not — anyone holding a captured response can delete the attribute without
/// invalidating anything, and "only compare it if present" then means "do not compare it". SAML 2.0
/// Bindings §3.5.5.2 requires the attribute on a signed message and Core §3.2.2 requires the recipient
/// to compare it; the requirement is enforced here against the Response signature, which is what covers
/// the attribute.
/// </remarks>
public sealed class SamlResponseFreshnessTests
{
    private const string Acs = "https://sp.test/saml/c1/acs";
    private const string Audience = "https://sp.test/saml/c1";

    private static SamlParseResult Parse(string base64Response, TimeSpan? maxAssertionAge = null) =>
        new SamlResponseParser(NullLogger<SamlResponseParser>.Instance).Parse(
            base64Response,
            new SamlResponseValidationContext(
                Acs, Audience, null, [SamlTestHelper.TestCertificate],
                MaxAssertionAge: maxAssertionAge ?? default));

    /// <summary>Deletes Response@Destination, the way an attacker relaying a captured response would.</summary>
    private static string StripDestination(string base64Response)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(Encoding.UTF8.GetString(Convert.FromBase64String(base64Response)));
        ((XmlElement)doc.DocumentElement!).RemoveAttribute("Destination");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
    }

    // -----------------------------------------------------------------------
    // Destination
    // -----------------------------------------------------------------------

    /// <summary>A conforming response still works — both halves must not break real IdPs.</summary>
    [Fact]
    public void Conforming_response_is_accepted()
    {
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com"));

        Assert.True(result.Success, result.Error);
    }

    /// <summary>
    /// A Response-signed message with the attribute deleted must be refused. Before, deleting it
    /// skipped the comparison — and because the signature covers the attribute, its absence here is
    /// itself proof the message is not the one the IdP signed.
    /// </summary>
    [Fact]
    public void Signed_response_without_Destination_is_refused()
    {
        var stripped = StripDestination(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com"));

        var result = Parse(stripped);

        Assert.False(result.Success, "a signed Response with no Destination was accepted");
        Assert.Contains("Destination", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The assertion-only-signed shape with the Destination deleted: the message signature is gone, so
    /// the binding's presence rule no longer applies — but the SIGNED Recipient still has to name this
    /// ACS, and that is the check that keeps a response minted for another endpoint out.
    /// </summary>
    [Fact]
    public void AssertionSigned_response_still_binds_to_this_acs_through_Recipient()
    {
        // Same ACS: accepted, because Recipient matches even with Destination gone.
        var sameAcs = StripDestination(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com", signAssertion: true));
        Assert.True(Parse(sameAcs).Success);

        // Minted for a different ACS: refused on the signed Recipient, which cannot be stripped.
        var otherAcs = StripDestination(SamlTestHelper.BuildSignedResponse(
            "https://other.test/saml/x/acs", Audience, "user@example.com",
            email: "user@example.com", signAssertion: true));
        Assert.False(Parse(otherAcs).Success);
    }

    /// <summary>A Destination naming another endpoint is still refused when it is present.</summary>
    [Fact]
    public void Destination_for_another_endpoint_is_refused()
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(Encoding.UTF8.GetString(Convert.FromBase64String(
            SamlTestHelper.BuildSignedResponse(
                Acs, Audience, "user@example.com", email: "user@example.com", signAssertion: true))));
        doc.DocumentElement!.SetAttribute("Destination", "https://elsewhere.test/acs");

        var result = Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml)));

        Assert.False(result.Success);
    }

    // -----------------------------------------------------------------------
    // Assertion IssueInstant
    // -----------------------------------------------------------------------

    /// <summary>
    /// An assertion whose confirmation window is still open but which was minted hours ago is refused.
    /// That is the shape a compromised or misconfigured IdP produces — a long NotOnOrAfter relative to
    /// IssueInstant — and without an SP-side cap the IdP alone decided how long a captured assertion
    /// stayed replayable.
    /// </summary>
    [Fact]
    public void Assertion_older_than_the_cap_is_refused_even_with_an_open_window()
    {
        var stale = SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            // Window open for another 6 hours; minted 5 hours ago.
            validFor: TimeSpan.FromHours(6), assertionAge: TimeSpan.FromHours(5));

        var result = Parse(stale);

        Assert.False(result.Success, "an assertion minted 5 hours ago was accepted");
        Assert.Contains("age", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An assertion minted moments ago is unaffected, which is every real web-SSO login.</summary>
    [Fact]
    public void Fresh_assertion_is_accepted()
    {
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            validFor: TimeSpan.FromHours(6), assertionAge: TimeSpan.FromSeconds(2)));

        Assert.True(result.Success, result.Error);
    }

    /// <summary>
    /// An IssueInstant far in the future is refused too: without it, backdating is trivially replaced by
    /// forward-dating to keep an assertion inside the age cap indefinitely.
    /// </summary>
    [Fact]
    public void Assertion_issued_in_the_future_is_refused()
    {
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            validFor: TimeSpan.FromHours(6), assertionAge: TimeSpan.FromHours(-2)));

        Assert.False(result.Success, "an assertion dated two hours in the future was accepted");
    }

    /// <summary>
    /// The replay record's retention follows the tighter of the two bounds. An IdP naming a six-hour
    /// window cannot make this SP hold assertion ids for six hours — the age cap governs, and the two
    /// must agree or the id is forgotten while the assertion is still acceptable.
    /// </summary>
    [Fact]
    public void AcceptableUntil_is_bounded_by_the_age_cap_not_by_the_idps_window()
    {
        var result = Parse(SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            validFor: TimeSpan.FromHours(6)));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.AcceptableUntil);
        // Age cap (1h) + clock skew (5m) — well short of the six hours the IdP asked for.
        Assert.True(result.AcceptableUntil < DateTimeOffset.UtcNow.AddHours(2),
            $"retention deadline {result.AcceptableUntil} follows the IdP's window, not the cap");
    }

    /// <summary>The cap is configurable, and a connection that widens it gets what it asked for.</summary>
    [Fact]
    public void MaxAssertionAge_is_configurable()
    {
        var stale = SamlTestHelper.BuildSignedResponse(
            Acs, Audience, "user@example.com", email: "user@example.com",
            validFor: TimeSpan.FromHours(12), assertionAge: TimeSpan.FromHours(5));

        Assert.False(Parse(stale).Success);
        Assert.True(Parse(stale, maxAssertionAge: TimeSpan.FromHours(8)).Success);
    }
}
