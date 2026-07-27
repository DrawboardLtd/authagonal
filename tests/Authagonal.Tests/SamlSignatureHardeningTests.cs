using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Authagonal.Server.Services.Saml;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Structural defences around XML signature verification, as distinct from "is the signature
/// cryptographically valid" (<see cref="SamlSignatureTests"/>).
/// </summary>
/// <remarks>
/// Checking the reference URI against the signed element's ID is necessary but not sufficient. The
/// transform chain decides which bytes are actually digested, and the resolver decides which element
/// <c>#id</c> means — so a signature can name the right element and still cover something else. These
/// tests exist because a reviewer reading only the URI check would be right to call it incomplete.
/// </remarks>
public sealed class SamlSignatureHardeningTests
{
    private const string Acs = "https://acme.authagonal.test/saml/acme/acs";
    private const string Audience = "https://acme.authagonal.test/saml/acme/metadata";
    private static readonly SamlResponseParser Parser = new(NullLogger<SamlResponseParser>.Instance);

    private static X509Certificate2 NewCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=idp-hardening-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static SamlResponseValidationContext Ctx(params X509Certificate2[] trusted) =>
        new(Acs, Audience, ExpectedInResponseTo: null, TrustedCertificates: trusted);

    private static XmlDocument BuildResponseDocument(string assertionId = "_assertion-1", string email = "user@acme.com")
    {
        var now = DateTimeOffset.UtcNow;
        var notOnOrAfter = now.AddMinutes(5).ToString("o");
        var xml = $"""
        <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="_resp-1" Version="2.0" IssueInstant="{now:o}" Destination="{Acs}">
          <saml:Issuer>https://idp.example.com</saml:Issuer>
          <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
          <saml:Assertion ID="{assertionId}" Version="2.0" IssueInstant="{now:o}">
            <saml:Issuer>https://idp.example.com</saml:Issuer>
            <saml:Subject>
              <saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">{email}</saml:NameID>
              <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                <saml:SubjectConfirmationData Recipient="{Acs}" NotOnOrAfter="{notOnOrAfter}"/>
              </saml:SubjectConfirmation>
            </saml:Subject>
            <saml:Conditions NotBefore="{now.AddMinutes(-5):o}" NotOnOrAfter="{notOnOrAfter}">
              <saml:AudienceRestriction><saml:Audience>{Audience}</saml:Audience></saml:AudienceRestriction>
            </saml:Conditions>
            <saml:AuthnStatement AuthnInstant="{now:o}" SessionIndex="sess-1">
              <saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:Password</saml:AuthnContextClassRef></saml:AuthnContext>
            </saml:AuthnStatement>
            <saml:AttributeStatement>
              <saml:Attribute Name="email"><saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute>
            </saml:AttributeStatement>
          </saml:Assertion>
        </samlp:Response>
        """;
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        return doc;
    }

    private static XmlElement Assertion(XmlDocument doc)
    {
        var nsm = new XmlNamespaceManager(doc.NameTable);
        nsm.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
        return (XmlElement)doc.SelectSingleNode("//saml:Assertion", nsm)!;
    }

    /// Signs <paramref name="element"/> in place, letting the caller shape the reference — which is how
    /// these tests produce signatures no honest IdP would emit.
    private static void SignElement(
        XmlDocument doc, XmlElement element, string id, X509Certificate2 cert,
        Action<Reference>? customizeReference = null, Reference? extraReference = null)
    {
        var signedXml = new IdSignedXml(doc) { SigningKey = cert.GetRSAPrivateKey() };
        var reference = new Reference("#" + id);
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        reference.DigestMethod = SignedXml.XmlDsigSHA256Url;
        customizeReference?.Invoke(reference);
        signedXml.AddReference(reference);
        if (extraReference is not null) signedXml.AddReference(extraReference);
        signedXml.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        signedXml.ComputeSignature();
        element.AppendChild(doc.ImportNode(signedXml.GetXml(), true));
    }

    private sealed class IdSignedXml(XmlDocument doc) : SignedXml(doc)
    {
        public override XmlElement? GetIdElement(XmlDocument? document, string idValue) =>
            (document ?? doc).SelectSingleNode($"//*[@ID='{idValue}']") as XmlElement
            ?? base.GetIdElement(document, idValue);
    }

    private static string Encode(XmlDocument doc) => Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));

    // ── Baseline ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void OrdinaryEnvelopedSignature_IsAccepted()
    {
        using var cert = NewCert();
        var doc = BuildResponseDocument();
        SignElement(doc, Assertion(doc), "_assertion-1", cert);

        var result = Parser.Parse(Encode(doc), Ctx(cert));
        Assert.True(result.Success, result.Error);
    }

    // ── Transform allowlist ──────────────────────────────────────────────────────────────────────
    // Both of these already failed before the allowlist existed: .NET refuses the XSLT and XPath chains
    // constructible here on its own. They are kept as policy tests, not as proof of a closed hole — they
    // pin the behaviour so it stays a property of our code rather than of the runtime.

    /// An XSLT transform would run attacker-supplied stylesheet code inside the verifier. No IdP sends one.
    [Fact]
    public void XsltTransform_IsRejected()
    {
        using var cert = NewCert();
        var doc = BuildResponseDocument();

        var xsltDoc = new XmlDocument();
        xsltDoc.LoadXml("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="1.0">
              <xsl:template match="/"><xsl:copy-of select="."/></xsl:template>
            </xsl:stylesheet>
            """);
        var xslt = new XmlDsigXsltTransform();
        xslt.LoadInnerXml(xsltDoc.ChildNodes);

        SignElement(doc, Assertion(doc), "_assertion-1", cert, customizeReference: r =>
        {
            r.TransformChain.Add(xslt);
        });

        var result = Parser.Parse(Encode(doc), Ctx(cert));
        Assert.False(result.Success);
    }

    /// An XPath transform re-selects what gets digested, so the reference URI can name the right
    /// element while the signature covers something else entirely.
    [Fact]
    public void XPathTransform_IsRejected()
    {
        using var cert = NewCert();
        var doc = BuildResponseDocument();

        var xpathDoc = new XmlDocument();
        xpathDoc.LoadXml("<XPath xmlns=\"http://www.w3.org/2000/09/xmldsig#\">true()</XPath>");
        var xpath = new XmlDsigXPathTransform();
        xpath.LoadInnerXml(xpathDoc.ChildNodes);

        SignElement(doc, Assertion(doc), "_assertion-1", cert, customizeReference: r =>
        {
            r.TransformChain.Add(xpath);
        });

        var result = Parser.Parse(Encode(doc), Ctx(cert));
        Assert.False(result.Success);
    }

    // ── Reference count ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void MultipleReferences_AreRejected()
    {
        using var cert = NewCert();
        var doc = BuildResponseDocument();

        // A second reference covering the Response as well as the Assertion. Only the first was ever
        // checked against the target element's ID.
        var extra = new Reference("#_resp-1");
        extra.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        extra.AddTransform(new XmlDsigExcC14NTransform());
        extra.DigestMethod = SignedXml.XmlDsigSHA256Url;

        SignElement(doc, Assertion(doc), "_assertion-1", cert, extraReference: extra);

        var result = Parser.Parse(Encode(doc), Ctx(cert));
        Assert.False(result.Success);
    }

    // ── Duplicate IDs ────────────────────────────────────────────────────────────────────────────
    /// Two elements sharing an ID make "#id" ambiguous: the reference-URI check compares strings, while
    /// CheckSignature resolves the ID across the document. Those can pick different elements, which is
    /// the shape of every classic signature-wrapping bug — so the document is refused outright.
    [Fact]
    public void DuplicateIds_AreRejected()
    {
        using var cert = NewCert();
        var doc = BuildResponseDocument();
        var assertion = Assertion(doc);
        SignElement(doc, assertion, "_assertion-1", cert);

        // Everything about this response is otherwise valid — the signature verifies, and both the
        // parser and the reference resolver would pick the genuine assertion. The only defect is a
        // second element carrying the same ID, which makes "#_assertion-1" ambiguous. Ambiguity is the
        // precondition for wrapping, so the document is refused rather than resolved by luck of
        // document order.
        var decoy = doc.CreateElement("saml", "Advice", "urn:oasis:names:tc:SAML:2.0:assertion");
        decoy.SetAttribute("ID", "_assertion-1");
        doc.DocumentElement!.AppendChild(decoy);

        var result = Parser.Parse(Encode(doc), Ctx(cert));
        Assert.False(result.Success);
    }
}
