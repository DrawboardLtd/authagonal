using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Generates valid signed SAML responses and IdP metadata for testing.
/// Uses self-signed certificates for assertion signing.
/// </summary>
public static class SamlTestHelper
{
    private static readonly Lazy<(X509Certificate2 Cert, RSA Key)> _testCert = new(() =>
    {
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test IdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (cert, rsa);
    });

    public static X509Certificate2 TestCertificate => _testCert.Value.Cert;
    public static RSA TestKey => _testCert.Value.Key;

    /// <summary>Build a signed SAML Response with a valid assertion.</summary>
    public static string BuildSignedResponse(
        string acsUrl,
        string audience,
        string nameId,
        string? inResponseTo = null,
        string? issuer = "https://idp.test",
        string? email = null,
        string? firstName = null,
        string? lastName = null,
        TimeSpan? validFor = null)
    {
        var now = DateTime.UtcNow;
        var notBefore = now.AddMinutes(-5);
        var notOnOrAfter = now.Add(validFor ?? TimeSpan.FromMinutes(10));
        var responseId = $"_response_{Guid.NewGuid():N}";
        var assertionId = $"_assertion_{Guid.NewGuid():N}";

        var xml = new XmlDocument { PreserveWhitespace = true };

        var sb = new StringBuilder();
        sb.Append($@"<samlp:Response xmlns:samlp=""urn:oasis:names:tc:SAML:2.0:protocol""
            xmlns:saml=""urn:oasis:names:tc:SAML:2.0:assertion""
            ID=""{responseId}""
            Version=""2.0""
            IssueInstant=""{now:O}""
            Destination=""{acsUrl}""");
        if (inResponseTo is not null)
            sb.Append($@" InResponseTo=""{inResponseTo}""");
        sb.Append(">");

        sb.Append($@"<saml:Issuer>{issuer}</saml:Issuer>");
        sb.Append(@"<samlp:Status><samlp:StatusCode Value=""urn:oasis:names:tc:SAML:2.0:status:Success""/></samlp:Status>");

        sb.Append($@"<saml:Assertion xmlns:saml=""urn:oasis:names:tc:SAML:2.0:assertion""
            ID=""{assertionId}""
            Version=""2.0""
            IssueInstant=""{now:O}"">");
        sb.Append($@"<saml:Issuer>{issuer}</saml:Issuer>");

        sb.Append($@"<saml:Subject>
            <saml:NameID Format=""urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress"">{nameId}</saml:NameID>
            <saml:SubjectConfirmation Method=""urn:oasis:names:tc:SAML:2.0:cm:bearer"">
                <saml:SubjectConfirmationData Recipient=""{acsUrl}"" NotOnOrAfter=""{notOnOrAfter:O}""/>
            </saml:SubjectConfirmation>
        </saml:Subject>");

        sb.Append($@"<saml:Conditions NotBefore=""{notBefore:O}"" NotOnOrAfter=""{notOnOrAfter:O}"">
            <saml:AudienceRestriction><saml:Audience>{audience}</saml:Audience></saml:AudienceRestriction>
        </saml:Conditions>");

        sb.Append($@"<saml:AuthnStatement AuthnInstant=""{now:O}"">
            <saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext>
        </saml:AuthnStatement>");

        sb.Append("<saml:AttributeStatement>");
        if (email is not null)
            sb.Append($@"<saml:Attribute Name=""http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress""><saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute>");
        if (firstName is not null)
            sb.Append($@"<saml:Attribute Name=""http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname""><saml:AttributeValue>{firstName}</saml:AttributeValue></saml:Attribute>");
        if (lastName is not null)
            sb.Append($@"<saml:Attribute Name=""http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname""><saml:AttributeValue>{lastName}</saml:AttributeValue></saml:Attribute>");
        sb.Append("</saml:AttributeStatement>");

        sb.Append("</saml:Assertion>");
        sb.Append("</samlp:Response>");

        xml.LoadXml(sb.ToString());

        // Sign the response
        SignXmlElement(xml, responseId, TestCertificate);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(xml.OuterXml));
    }

    /// <summary>Build a SAML Response with a failed status.</summary>
    public static string BuildFailedResponse(string? inResponseTo = null)
    {
        var xml = $@"<samlp:Response xmlns:samlp=""urn:oasis:names:tc:SAML:2.0:protocol""
            ID=""_fail_{Guid.NewGuid():N}""
            Version=""2.0""
            IssueInstant=""{DateTime.UtcNow:O}""
            {(inResponseTo is not null ? $@"InResponseTo=""{inResponseTo}""" : "")}>
            <samlp:Status>
                <samlp:StatusCode Value=""urn:oasis:names:tc:SAML:2.0:status:Responder""/>
            </samlp:Status>
        </samlp:Response>";

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
    }

    /// <summary>Build IdP metadata XML with the test signing certificate.</summary>
    public static string BuildIdpMetadata(string entityId = "https://idp.test", string ssoUrl = "https://idp.test/sso")
    {
        var certBase64 = Convert.ToBase64String(TestCertificate.Export(X509ContentType.Cert));

        return $@"<?xml version=""1.0""?>
<EntityDescriptor xmlns=""urn:oasis:names:tc:SAML:2.0:metadata""
    entityID=""{entityId}"">
    <IDPSSODescriptor protocolSupportEnumeration=""urn:oasis:names:tc:SAML:2.0:protocol"">
        <KeyDescriptor use=""signing"">
            <KeyInfo xmlns=""http://www.w3.org/2000/09/xmldsig#"">
                <X509Data><X509Certificate>{certBase64}</X509Certificate></X509Data>
            </KeyInfo>
        </KeyDescriptor>
        <SingleSignOnService
            Binding=""urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect""
            Location=""{ssoUrl}""/>
    </IDPSSODescriptor>
</EntityDescriptor>";
    }

    private static void SignXmlElement(XmlDocument doc, string elementId, X509Certificate2 cert)
    {
        var signedXml = new SignedXml(doc)
        {
            SigningKey = cert.GetRSAPrivateKey()
        };

        var reference = new Reference($"#{elementId}");
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();

        // Insert signature after Issuer element
        var responseElement = doc.DocumentElement!;
        var issuerNode = responseElement.GetElementsByTagName("Issuer", "urn:oasis:names:tc:SAML:2.0:assertion")[0];
        responseElement.InsertAfter(doc.ImportNode(signedXml.GetXml(), true), issuerNode);
    }
}
