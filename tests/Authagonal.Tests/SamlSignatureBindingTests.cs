using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// A redirect-binding signature only vouches for the message the handler is going to act on.
/// </summary>
/// <remarks>
/// <c>SamlRedirectBinding.Verify</c> chose the message itself —
/// <c>Find("SAMLRequest") ?? Find("SAMLResponse")</c> — and the caller never said which parameter it had
/// decoded. The LogoutResponse leg decodes <c>Query["SAMLResponse"]</c> and handed the whole query string to
/// a verifier that preferred <c>SAMLRequest</c> whenever one was present.
/// <para>
/// The parameter name is inside the signed bytes, so a captured signature cannot be MOVED between
/// parameters — but it never had to be. Keep a captured, correctly-signed
/// <c>SAMLRequest=…&amp;SigAlg=…&amp;Signature=…</c> triple intact and append a forged
/// <c>SAMLResponse=</c>: verification passed over the captured triple and the handler processed the appended,
/// entirely attacker-authored message.
/// </para>
/// </remarks>
public sealed class SamlSignatureBindingTests
{
    private static (string Query, X509Certificate2 Cert) SignedAuthnRequest()
    {
        var url = SamlRequestBuilder.BuildAuthnRequestUrl("_r1", "sp", "https://sp/acs", "https://idp/sso");
        return (new Uri(SamlRedirectBinding.Sign(url, SamlTestHelper.TestKey)).Query,
                SamlTestHelper.TestCertificate);
    }

    /// <summary>
    /// A retired IdP certificate cannot authenticate a redirect-binding message.
    /// </summary>
    /// <remarks>
    /// <see cref="SamlResponseParser"/> enforces the certificate's own validity window for XML signatures
    /// over the same pinned trust set. This verifier went straight to <c>VerifyData</c>, so a certificate the
    /// IdP had rotated away from — retirement after a compromise being the case the check exists for — kept
    /// authenticating redirect-binding messages indefinitely. On this binding that means forcing logout
    /// through <c>/saml/{connection}/logout</c> and <c>/saml/{connection}/slo</c>.
    /// <para>
    /// The only other expiry control here is metadata <c>@validUntil</c>, which is optional and which a
    /// pasted <c>MetadataXml</c> connection never re-fetches — so for those connections the certificate set
    /// was frozen with no expiry at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnExpiredIdpCertificateCannotVerifyARedirectBindingSignature()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Retired IdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expired = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddYears(-3), DateTimeOffset.UtcNow.AddYears(-2));

        var url = SamlRequestBuilder.BuildAuthnRequestUrl("_r1", "sp", "https://sp/acs", "https://idp/sso");
        var query = new Uri(SamlRedirectBinding.Sign(url, rsa)).Query;

        // The signature itself is genuine — it is the certificate that is no longer current.
        Assert.False(SamlRedirectBinding.Verify(query, "SAMLRequest", [expired]));
    }

    /// <summary>The control: the same message verifies against a certificate that IS current.</summary>
    [Fact]
    public void ACurrentIdpCertificateStillVerifiesARedirectBindingSignature()
    {
        var (captured, cert) = SignedAuthnRequest();
        Assert.True(SamlRedirectBinding.Verify(captured, "SAMLRequest", [cert]));
    }

    /// <summary>The captured-triple-plus-appended-response attack.</summary>
    [Fact]
    public void AppendingAForgedResponseToACapturedRequestIsRefused()
    {
        var (captured, cert) = SignedAuthnRequest();

        // The signature is genuine and covers the SAMLRequest.
        Assert.True(SamlRedirectBinding.Verify(captured, "SAMLRequest", [cert]));

        // Append an attacker-authored SAMLResponse. Nothing signed it.
        var attack = captured + "&SAMLResponse=" + Uri.EscapeDataString("forged-logout-response");

        // Asked about the parameter the LogoutResponse leg actually decodes, this must fail.
        Assert.False(SamlRedirectBinding.Verify(attack, "SAMLResponse", [cert]));

        // And carrying both messages is refused outright — SAML Bindings §3.4.4.1 permits exactly one.
        Assert.False(SamlRedirectBinding.Verify(attack, "SAMLRequest", [cert]));
    }

    /// <summary>A duplicated message parameter is refused rather than resolved by position.</summary>
    /// <remarks>
    /// <c>Verify</c> scans for the FIRST occurrence; ASP.NET's query parser may hand the handler the last. A
    /// query carrying two of them is a request about which the two disagree, so neither answer is safe.
    /// </remarks>
    [Fact]
    public void ADuplicatedMessageParameterIsRefused()
    {
        var (captured, cert) = SignedAuthnRequest();

        Assert.False(SamlRedirectBinding.Verify(
            captured + "&SAMLRequest=" + Uri.EscapeDataString("second-message"), "SAMLRequest", [cert]));
    }

    /// <summary>Duplicated SigAlg or Signature is refused for the same reason.</summary>
    [Theory]
    [InlineData("SigAlg")]
    [InlineData("Signature")]
    [InlineData("RelayState")]
    public void ADuplicatedSignatureParameterIsRefused(string name)
    {
        var (captured, cert) = SignedAuthnRequest();

        Assert.False(SamlRedirectBinding.Verify(
            captured + $"&{name}=" + Uri.EscapeDataString("x"), "SAMLRequest", [cert]));
    }

    /// <summary>The control: an ordinary signed request still verifies.</summary>
    [Fact]
    public void AnOrdinarySignedRequestStillVerifies()
    {
        var (captured, cert) = SignedAuthnRequest();

        Assert.True(SamlRedirectBinding.Verify(captured, "SAMLRequest", [cert]));
    }

    // ── certificate validity ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An expired IdP signing certificate no longer validates a signature.
    /// </summary>
    /// <remarks>
    /// Chain building and revocation are deliberately skipped — trust comes from pinning the IdP's metadata
    /// certificates, which is correct — but <c>NotBefore</c>/<c>NotAfter</c> are a statement the certificate
    /// makes about itself, and nothing consulted them at load time or at validation. Verified against the
    /// shipped code: a signature made with a certificate that expired two years ago validated.
    /// <para>
    /// The only other expiry control on this path is metadata <c>@validUntil</c>, which SAML 2.0 Metadata
    /// makes optional, several major IdPs omit, and a PASTED connection never re-fetches — so for those
    /// connections the certificate set was frozen with no expiry at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnExpiredIdpCertificateNoLongerValidatesASignature()
    {
        var (doc, cert) = SignedDocument(
            notBefore: DateTimeOffset.UtcNow.AddYears(-3),
            notAfter: DateTimeOffset.UtcNow.AddYears(-2));

        Assert.False(SamlResponseParser.ValidateElementSignature(
            doc.DocumentElement!, [cert], NullLogger.Instance));
    }

    /// <summary>A certificate whose validity has not started yet is refused the same way.</summary>
    [Fact]
    public void ANotYetValidIdpCertificateIsRefused()
    {
        var (doc, cert) = SignedDocument(
            notBefore: DateTimeOffset.UtcNow.AddDays(30),
            notAfter: DateTimeOffset.UtcNow.AddDays(60));

        Assert.False(SamlResponseParser.ValidateElementSignature(
            doc.DocumentElement!, [cert], NullLogger.Instance));
    }

    /// <summary>
    /// The control: the same signature validates while the certificate is inside its window.
    /// </summary>
    /// <remarks>
    /// Without this, a validity check that refused everything — a timezone slip, a comparison the wrong way
    /// round — would pass both tests above.
    /// </remarks>
    [Fact]
    public void ACurrentIdpCertificateStillValidatesASignature()
    {
        var (doc, cert) = SignedDocument(
            notBefore: DateTimeOffset.UtcNow.AddDays(-1),
            notAfter: DateTimeOffset.UtcNow.AddYears(1));

        Assert.True(SamlResponseParser.ValidateElementSignature(
            doc.DocumentElement!, [cert], NullLogger.Instance));
    }

    /// <summary>
    /// A minimal enveloped-signature document signed by a certificate with the given validity window.
    /// </summary>
    private static (XmlDocument Document, X509Certificate2 Certificate) SignedDocument(
        DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Validity Window IdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(notBefore, notAfter);

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml("""<Message ID="_m1" xmlns="urn:test">payload</Message>""");

        var signedXml = new SignedXml(doc) { SigningKey = rsa };
        var reference = new Reference("#_m1");
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        signedXml.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        signedXml.ComputeSignature();

        doc.DocumentElement!.AppendChild(doc.ImportNode(signedXml.GetXml(), true));
        return (doc, cert);
    }
}
