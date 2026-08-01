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
        TimeSpan? validFor = null,
        string? extraAttributesXml = null,
        string? sessionIndex = null,
        bool signAssertion = false,
        SubjectConfirmationShape confirmationShape = SubjectConfirmationShape.Conforming,
        // F260 — a signed Response with no Destination is one the IdP minted for whoever holds it.
        bool includeDestination = true,
        // F260 — an extra <Conditions> child, to prove an unevaluable condition is not silently dropped.
        string? extraConditionsXml = null,
        // F285 — Web Browser SSO §4.1.4.2 requires an AuthnStatement; omitting it used to parse fine.
        bool includeAuthnStatement = true,
        // Leaves the document unsigned, for callers that sign it themselves after transforming it —
        // see SignResponseAfterEncryption.
        bool sign = true,
        // Backdates the ASSERTION's IssueInstant only, leaving every NotBefore/NotOnOrAfter where it is:
        // the shape of an IdP whose stated validity window is long relative to when it minted the
        // assertion, which is what the absolute age cap exists to bound.
        TimeSpan? assertionAge = null)
    {
        var now = DateTime.UtcNow;
        var assertionIssueInstant = now - (assertionAge ?? TimeSpan.Zero);
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
            IssueInstant=""{now:O}""");
        if (includeDestination)
            sb.Append($@" Destination=""{acsUrl}""");
        if (inResponseTo is not null)
            sb.Append($@" InResponseTo=""{inResponseTo}""");
        sb.Append(">");

        sb.Append($@"<saml:Issuer>{issuer}</saml:Issuer>");
        sb.Append(@"<samlp:Status><samlp:StatusCode Value=""urn:oasis:names:tc:SAML:2.0:status:Success""/></samlp:Status>");

        sb.Append($@"<saml:Assertion xmlns:saml=""urn:oasis:names:tc:SAML:2.0:assertion""
            ID=""{assertionId}""
            Version=""2.0""
            IssueInstant=""{assertionIssueInstant:O}"">");
        sb.Append($@"<saml:Issuer>{issuer}</saml:Issuer>");

        // The bearer confirmation, in whichever shape the test asked for. Every non-conforming variant here
        // was ACCEPTED before the parser stopped treating these checks as "only if present".
        var confirmation = confirmationShape switch
        {
            SubjectConfirmationShape.Conforming =>
                $@"<saml:SubjectConfirmation Method=""urn:oasis:names:tc:SAML:2.0:cm:bearer"">
                <saml:SubjectConfirmationData Recipient=""{acsUrl}"" NotOnOrAfter=""{notOnOrAfter:O}""/>
            </saml:SubjectConfirmation>",
            SubjectConfirmationShape.Absent => "",
            SubjectConfirmationShape.NoConfirmationData =>
                @"<saml:SubjectConfirmation Method=""urn:oasis:names:tc:SAML:2.0:cm:bearer""/>",
            SubjectConfirmationShape.NoRecipient =>
                $@"<saml:SubjectConfirmation Method=""urn:oasis:names:tc:SAML:2.0:cm:bearer"">
                <saml:SubjectConfirmationData NotOnOrAfter=""{notOnOrAfter:O}""/>
            </saml:SubjectConfirmation>",
            SubjectConfirmationShape.NoNotOnOrAfter =>
                $@"<saml:SubjectConfirmation Method=""urn:oasis:names:tc:SAML:2.0:cm:bearer"">
                <saml:SubjectConfirmationData Recipient=""{acsUrl}""/>
            </saml:SubjectConfirmation>",
            SubjectConfirmationShape.UnparseableNotOnOrAfter =>
                $@"<saml:SubjectConfirmation Method=""urn:oasis:names:tc:SAML:2.0:cm:bearer"">
                <saml:SubjectConfirmationData Recipient=""{acsUrl}"" NotOnOrAfter=""not-a-timestamp""/>
            </saml:SubjectConfirmation>",
            _ => throw new ArgumentOutOfRangeException(nameof(confirmationShape)),
        };

        sb.Append($@"<saml:Subject>
            <saml:NameID Format=""urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress"">{nameId}</saml:NameID>
            {confirmation}
        </saml:Subject>");

        sb.Append($@"<saml:Conditions NotBefore=""{notBefore:O}"" NotOnOrAfter=""{notOnOrAfter:O}"">
            <saml:AudienceRestriction><saml:Audience>{audience}</saml:Audience></saml:AudienceRestriction>
            {extraConditionsXml}
        </saml:Conditions>");

        if (includeAuthnStatement)
        {
            var sessionIndexAttr = sessionIndex is null ? "" : $@" SessionIndex=""{sessionIndex}""";
            sb.Append($@"<saml:AuthnStatement AuthnInstant=""{now:O}""{sessionIndexAttr}>
                <saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext>
            </saml:AuthnStatement>");
        }

        sb.Append("<saml:AttributeStatement>");
        if (email is not null)
            sb.Append($@"<saml:Attribute Name=""http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress""><saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute>");
        if (firstName is not null)
            sb.Append($@"<saml:Attribute Name=""http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname""><saml:AttributeValue>{firstName}</saml:AttributeValue></saml:Attribute>");
        if (lastName is not null)
            sb.Append($@"<saml:Attribute Name=""http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname""><saml:AttributeValue>{lastName}</saml:AttributeValue></saml:Attribute>");
        if (extraAttributesXml is not null)
            sb.Append(extraAttributesXml);
        sb.Append("</saml:AttributeStatement>");

        sb.Append("</saml:Assertion>");
        sb.Append("</samlp:Response>");

        xml.LoadXml(sb.ToString());

        if (!sign)
        {
            // Left to the caller.
        }
        else if (signAssertion)
        {
            // Sign the Assertion in place (the Entra/ADFS shape). Note this must happen BEFORE
            // encryption: encrypting an already-signed assertion is what ADFS does, and the signature
            // travels inside the ciphertext. The other supported shape — sign the Response AFTER
            // encrypting the assertion, so the signature covers the EncryptedAssertion — is built by
            // SignResponseAfterEncryption below.
            var nsm = new XmlNamespaceManager(xml.NameTable);
            nsm.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
            var assertion = (XmlElement)xml.SelectSingleNode("//saml:Assertion", nsm)!;
            SignElementInPlace(xml, assertion, assertionId, TestCertificate);
        }
        else
        {
            // Sign the response
            SignXmlElement(xml, responseId, TestCertificate);
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(xml.OuterXml));
    }

    /// <summary>
    /// F54: replace the (signed) Assertion with an EncryptedAssertion encrypted to the given cert,
    /// the way ADFS does once the SP metadata advertises an encryption KeyDescriptor.
    /// </summary>
    /// <summary>
    /// Wraps the response's Assertion in an EncryptedAssertion.
    /// </summary>
    /// <param name="useRsa15">
    /// When true, wraps the content key with RSA-PKCS#1 v1.5 — which the parser now REFUSES, because v1.5
    /// unwrapping on an anonymous endpoint is a Bleichenbacher decryption oracle against the SP private
    /// key. Note that .NET's <c>EncryptedXml.Encrypt(element, cert)</c> uses v1.5, so every encrypted-
    /// assertion test here was previously exercising only the vulnerable algorithm.
    /// </param>
    public static string EncryptAssertionInResponse(
        string base64Response, X509Certificate2 encryptionCert, bool useRsa15 = false)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(Encoding.UTF8.GetString(Convert.FromBase64String(base64Response)));

        var nsm = new XmlNamespaceManager(doc.NameTable);
        nsm.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
        var assertion = (XmlElement)doc.SelectSingleNode("//saml:Assertion", nsm)!;

        EncryptedData encryptedData;
        if (useRsa15)
        {
            var encryptedXml = new EncryptedXml();
            encryptedData = encryptedXml.Encrypt(assertion, encryptionCert);
        }
        else
        {
            // Build the OAEP-wrapped form real IdPs emit: AES-256-CBC content key, RSA-OAEP key transport.
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();

            var encryptedXml = new EncryptedXml();
            var cipher = encryptedXml.EncryptData(assertion, aes, content: false);

            encryptedData = new EncryptedData
            {
                Type = EncryptedXml.XmlEncElementUrl,
                EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncAES256Url),
                CipherData = new CipherData(cipher),
            };

            using var rsa = encryptionCert.GetRSAPublicKey()!;
            var wrapped = rsa.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA256);

            var encryptedKey = new EncryptedKey
            {
                EncryptionMethod = new EncryptionMethod("http://www.w3.org/2009/xmlenc11#rsa-oaep"),
                CipherData = new CipherData(wrapped),
            };
            var keyXml = encryptedKey.GetXml();
            // Declare the OAEP digest, which the parser honours instead of trying paddings in turn.
            var digest = keyXml.OwnerDocument.CreateElement("DigestMethod", SignedXml.XmlDsigNamespaceUrl);
            digest.SetAttribute("Algorithm", SignedXml.XmlDsigSHA256Url);
            keyXml.SelectSingleNode("*[local-name()='EncryptionMethod']")!.AppendChild(digest);

            var ki = new KeyInfo();
            ki.AddClause(new KeyInfoEncryptedKey(new EncryptedKey
            {
                EncryptionMethod = encryptedKey.EncryptionMethod,
                CipherData = encryptedKey.CipherData,
            }));
            encryptedData.KeyInfo = ki;

            var encryptedAssertionOaep = doc.CreateElement("saml", "EncryptedAssertion", "urn:oasis:names:tc:SAML:2.0:assertion");
            encryptedAssertionOaep.AppendChild(doc.ImportNode(encryptedData.GetXml(), true));
            // Replace the library-serialized EncryptedKey with ours, so the DigestMethod is present.
            var libKey = encryptedAssertionOaep.SelectSingleNode(".//*[local-name()='EncryptedKey']");
            libKey!.ParentNode!.ReplaceChild(doc.ImportNode(keyXml, true), libKey);
            assertion.ParentNode!.ReplaceChild(encryptedAssertionOaep, assertion);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
        }

        var encryptedAssertion = doc.CreateElement("saml", "EncryptedAssertion", "urn:oasis:names:tc:SAML:2.0:assertion");
        encryptedAssertion.AppendChild(doc.ImportNode(encryptedData.GetXml(), true));
        assertion.ParentNode!.ReplaceChild(encryptedAssertion, assertion);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
    }

    private static void SignElementInPlace(XmlDocument doc, XmlElement element, string id, X509Certificate2 cert)
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

    /// <summary>Build a SAML Response with a failed status.</summary>
    /// <summary>
    /// Signs the Response of an already-encrypted document, producing the shape an IdP emits when it
    /// signs at the Response level and encrypts the assertion.
    /// </summary>
    /// <remarks>
    /// This combination could not previously validate at all: decryption calls
    /// <c>EncryptedXml.ReplaceData</c>, which rewrites the loaded document in place, and the Response
    /// signature was only checked afterwards — against a DOM that no longer matched what was signed.
    /// So responseSignatureValid was unconditionally false for every encrypted response.
    /// </remarks>
    public static string SignResponseAfterEncryption(string base64Response)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(Encoding.UTF8.GetString(Convert.FromBase64String(base64Response)));

        var responseId = doc.DocumentElement!.GetAttribute("ID");
        SignXmlElement(doc, responseId, TestCertificate);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
    }

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

    /// <summary>Build IdP metadata XML with the test signing certificate (or a supplied one).</summary>
    public static string BuildIdpMetadata(
        string entityId = "https://idp.test",
        string ssoUrl = "https://idp.test/sso",
        X509Certificate2? signingCert = null,
        string? sloUrl = null,
        bool wantAuthnRequestsSigned = false)
    {
        var certBase64 = Convert.ToBase64String((signingCert ?? TestCertificate).Export(X509ContentType.Cert));
        var wantSigned = wantAuthnRequestsSigned ? @" WantAuthnRequestsSigned=""true""" : "";
        var slo = sloUrl is null ? "" : $@"
        <SingleLogoutService
            Binding=""urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect""
            Location=""{sloUrl}""/>";

        return $@"<?xml version=""1.0""?>
<EntityDescriptor xmlns=""urn:oasis:names:tc:SAML:2.0:metadata""
    entityID=""{entityId}"">
    <IDPSSODescriptor{wantSigned} protocolSupportEnumeration=""urn:oasis:names:tc:SAML:2.0:protocol"">
        <KeyDescriptor use=""signing"">
            <KeyInfo xmlns=""http://www.w3.org/2000/09/xmldsig#"">
                <X509Data><X509Certificate>{certBase64}</X509Certificate></X509Data>
            </KeyInfo>
        </KeyDescriptor>{slo}
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
/// <summary>
/// Bearer <c>SubjectConfirmation</c> shapes, for pinning the fail-open that SAML 2.0 Profiles
/// §4.1.4.2/§4.1.4.3 forbids. The parser used to enforce every part of this only when present, so an
/// assertion missing any of it was accepted — and because SubjectConfirmationData/NotOnOrAfter is the SHORT
/// validity bound (minutes) while Conditions/NotOnOrAfter is the long one (~an hour), losing it let an
/// assertion stay acceptable long enough to outlive the replay cache and be replayed.
/// </summary>
public enum SubjectConfirmationShape
{
    Conforming,
    Absent,
    NoConfirmationData,
    NoRecipient,
    NoNotOnOrAfter,
    UnparseableNotOnOrAfter,
}
