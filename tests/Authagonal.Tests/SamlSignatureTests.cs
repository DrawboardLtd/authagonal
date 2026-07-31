using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Authagonal.Server.Services.Saml;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// SAML assertion signature verification — the security boundary of SSO login. A valid
/// signature from a trusted cert must be accepted; a tampered assertion, a signature from
/// an untrusted cert, or no signature must all be rejected (otherwise SSO is an auth bypass).
/// </summary>
public sealed class SamlSignatureTests
{
    private const string Acs = "https://acme.authagonal.test/saml/acme/acs";
    private const string Audience = "https://acme.authagonal.test/saml/acme/metadata";

    private static readonly SamlResponseParser Parser = new(NullLogger<SamlResponseParser>.Instance);

    private static X509Certificate2 NewCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test-idp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    // Build a SAML Response whose <Assertion> is enveloped-signed with `signingCert`.
    private static string BuildSignedResponse(
        X509Certificate2 signingCert,
        string email = "user@acme.com",
        bool sign = true,
        string responseVersion = "2.0",
        string assertionVersion = "2.0")
    {
        const string aid = "_assertion-1";
        var now = DateTimeOffset.UtcNow;
        var notOnOrAfter = now.AddMinutes(5).ToString("o");
        var xml = $"""
        <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="_resp-1" Version="{responseVersion}" IssueInstant="{now:o}" Destination="{Acs}">
          <saml:Issuer>https://idp.example.com</saml:Issuer>
          <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
          <saml:Assertion ID="{aid}" Version="{assertionVersion}" IssueInstant="{now:o}">
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

        if (sign)
        {
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
            var assertion = (XmlElement)doc.SelectSingleNode("//saml:Assertion", nsm)!;
            SignElement(doc, assertion, aid, signingCert);
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
    }

    private static void SignElement(XmlDocument doc, XmlElement element, string id, X509Certificate2 cert)
    {
        var signedXml = new IdSignedXml(doc) { SigningKey = cert.GetRSAPrivateKey() };
        var reference = new Reference("#" + id);
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        reference.DigestMethod = SignedXml.XmlDsigSHA256Url;
        signedXml.AddReference(reference);
        signedXml.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        // Insert the signature as a child of the signed element (Entra signs the Assertion in place).
        element.AppendChild(doc.ImportNode(signedXml.GetXml(), true));
    }

    // SignedXml resolves "#id" references via the SAML "ID" attribute (not the default "Id").
    private sealed class IdSignedXml : SignedXml
    {
        private readonly XmlDocument _doc;
        public IdSignedXml(XmlDocument doc) : base(doc) => _doc = doc;
        public override XmlElement? GetIdElement(XmlDocument? document, string idValue) =>
            (document ?? _doc).SelectSingleNode($"//*[@ID='{idValue}']") as XmlElement ?? base.GetIdElement(document, idValue);
    }

    private static SamlResponseValidationContext Ctx(params X509Certificate2[] trusted) =>
        new(Acs, Audience, ExpectedInResponseTo: null, TrustedCertificates: trusted);

    // ---------------------------------------------------------------------------------------------
    // F349 — Core §2.3.3 / §3.2.2 make Version REQUIRED and fix it at "2.0"
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Neither element's Version was ever read, so a document declaring another version — or none —
    /// was parsed with 2.0 semantics regardless. Core §4.1 obliges a responder that cannot process
    /// the version to say so rather than proceed. Both elements are checked separately: a decrypted
    /// EncryptedAssertion carries its own Version that the Response's says nothing about.
    /// </summary>
    [Theory]
    [InlineData("1.1", "2.0")]
    [InlineData("", "2.0")]
    [InlineData("2.0", "1.1")]
    [InlineData("2.0", "")]
    public void WrongOrMissingVersion_IsRejected(string responseVersion, string assertionVersion)
    {
        using var cert = NewCert();
        var response = BuildSignedResponse(
            cert, responseVersion: responseVersion, assertionVersion: assertionVersion);

        var result = Parser.Parse(response, Ctx(cert));

        Assert.False(result.Success);
        Assert.Contains("Version", result.Error);
    }

    [Fact]
    public void ValidSignature_FromTrustedCert_IsAccepted()
    {
        using var cert = NewCert();
        var result = Parser.Parse(BuildSignedResponse(cert), Ctx(cert));
        Assert.True(result.Success, result.Error);
        Assert.Equal("user@acme.com", result.NameId);
    }

    [Fact]
    public void TamperedAssertion_IsRejected()
    {
        using var cert = NewCert();
        var signed = Encoding.UTF8.GetString(Convert.FromBase64String(BuildSignedResponse(cert)));
        // Flip the NameID after signing — must invalidate the signature.
        var tampered = signed.Replace("user@acme.com", "attacker@evil.com");
        var result = Parser.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered)), Ctx(cert));
        Assert.False(result.Success);
    }

    [Fact]
    public void SignatureFromUntrustedCert_IsRejected()
    {
        using var signer = NewCert();
        using var other = NewCert();
        var result = Parser.Parse(BuildSignedResponse(signer), Ctx(other));
        Assert.False(result.Success);
    }

    [Fact]
    public void UnsignedResponse_IsRejected()
    {
        using var cert = NewCert();
        var result = Parser.Parse(BuildSignedResponse(cert, sign: false), Ctx(cert));
        Assert.False(result.Success);
    }
}
