using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Authagonal.Server.Services.Saml;

public sealed record SamlResponseValidationContext(
    string ExpectedAcsUrl,
    string ExpectedAudience,
    string? ExpectedInResponseTo,
    IReadOnlyList<X509Certificate2> TrustedCertificates,
    TimeSpan ClockSkew = default,
    System.Security.Cryptography.RSA? DecryptionKey = null)
{
    public TimeSpan ClockSkew { get; init; } = ClockSkew == default ? TimeSpan.FromMinutes(5) : ClockSkew;
}

public sealed record SamlParseResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? NameId { get; init; }
    public string? NameIdFormat { get; init; }
    /// <summary>First value per attribute, keyed case-insensitively by Name and FriendlyName.</summary>
    public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// All values per attribute (F50) — multi-valued attributes like groups/memberOf carry one
    /// AttributeValue element per entry. Keyed case-insensitively by Name and FriendlyName.
    /// </summary>
    public Dictionary<string, List<string>> AttributeValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SessionIndex { get; init; }

    /// <summary>
    /// <c>InResponseTo</c> as it appears inside the SIGNED assertion
    /// (<c>Subject/SubjectConfirmation/SubjectConfirmationData</c>), independent of the Response
    /// wrapper's own copy.
    /// </summary>
    /// <remarks>
    /// The wrapper's attribute is outside the signature when only the assertion is signed — which is
    /// the common IdP configuration — so an attacker who captured a response could delete it and turn
    /// an SP-initiated response into one the ACS accepted as "IdP-initiated", skipping request
    /// validation and replay consumption entirely. This copy is covered by the signature, so it
    /// cannot be stripped, and its presence is proof the flow really was SP-initiated.
    /// </remarks>
    public string? SignedInResponseTo { get; init; }
    public string? AssertionId { get; init; }

    /// <summary>
    /// The instant after which this assertion is no longer acceptable to us — the bearer
    /// SubjectConfirmationData's NotOnOrAfter plus the allowed clock skew.
    /// </summary>
    /// <remarks>
    /// The replay record for <see cref="AssertionId"/> must be retained at least this long (SAML 2.0
    /// Profiles §4.1.4.5: keep used ids "for the length of time for which the assertion would be considered
    /// valid"). The caches previously used a fixed TTL unrelated to the assertion, so an id could be
    /// forgotten while the assertion was still acceptable — at which point re-presenting it read as a first
    /// sighting.
    /// </remarks>
    public DateTimeOffset? AcceptableUntil { get; init; }
}

public sealed class SamlResponseParser(ILogger<SamlResponseParser> logger)
{
    /// <summary>
    /// The exact failure string produced when neither the Response nor the Assertion carried a
    /// validatable signature. The ACS matches on this to trigger a one-shot metadata refetch (F52:
    /// IdP cert rollover mid-cache-window would otherwise fail logins until the TTL lapses).
    /// </summary>
    public const string SignatureFailure = "No valid signature found on Response or Assertion.";

    /// <summary>
    /// The single message returned for EVERY assertion-decryption failure — wrong key, refused algorithm,
    /// malformed structure, bad padding, all of it.
    /// </summary>
    /// <remarks>
    /// The ACS is anonymous, unauthenticated and (before this) unthrottled, and it used to reflect the
    /// underlying <c>CryptographicException.Message</c> to the caller. Distinguishable failure responses
    /// are exactly what a Bleichenbacher attack on RSA key transport and a CBC padding-oracle attack on the
    /// data layer consume; with a distinguishing signal, recovering a captured assertion (or forging one
    /// raw RSA signature) costs on the order of 10^4-10^5 requests. Keeping the response constant removes
    /// the signal, and it is why the response must not vary by stage either.
    /// </remarks>
    public const string DecryptionFailure = "Could not decrypt the assertion.";

    /// <summary>
    /// Loads attacker-supplied SAML XML with DTD processing prohibited and entity expansion disabled.
    /// <c>PreserveWhitespace</c> is on because signature validation depends on it.
    /// </summary>
    /// <remarks>
    /// The ONLY way this project should turn SAML bytes into a document. Setting <c>XmlResolver = null</c>
    /// alone is not sufficient: it blocks external entities (XXE) but does nothing about expansion of
    /// entities declared in the internal subset, and <c>XmlDocument.LoadXml</c> builds an
    /// <c>XmlTextReader</c> whose <c>DtdProcessing</c> defaults to <c>Parse</c> with no cap on
    /// <c>MaxCharactersFromEntities</c>. A ~1 KB document with nine levels of nested internal entities
    /// therefore expanded to gigabytes — a "billion laughs" DoS reachable pre-authentication on the
    /// anonymous ACS. The endpoint's separate ad-hoc parse (used only to read InResponseTo before
    /// signature validation) had exactly that shape, so this helper is public and both callers use it.
    /// </remarks>
    public static XmlDocument LoadHardened(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
        };
        using var stringReader = new System.IO.StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, readerSettings);
        doc.Load(xmlReader);
        return doc;
    }

    /// <summary>XML Encryption 1.1 RSA-OAEP key transport, digest declared by a DigestMethod child.</summary>
    private const string Xmlenc11RsaOaepUrl = "http://www.w3.org/2009/xmlenc11#rsa-oaep";

    /// <summary>
    /// The OAEP padding declared by an <c>EncryptedKey</c>'s <c>EncryptionMethod/DigestMethod</c>, so the
    /// declared algorithm is honoured rather than discovered by trial. Returns a single padding whenever
    /// the digest is stated; only an ABSENT DigestMethod falls back to a two-element list, and that is a
    /// structural ambiguity in the document rather than attacker-selected — OAEP is not vulnerable to the
    /// adaptive chosen-ciphertext attack that makes a PKCS#1 v1.5 trial loop an oracle.
    /// </summary>
    private static System.Security.Cryptography.RSAEncryptionPadding[] OaepPaddingsFor(
        XmlElement encryptedKeyElement, XmlDocument doc, bool defaultToSha1)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("xenc", EncryptedXml.XmlEncNamespaceUrl);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

        var digest = (encryptedKeyElement.SelectSingleNode("xenc:EncryptionMethod/ds:DigestMethod", ns)
            as XmlElement)?.GetAttribute("Algorithm");

        return digest switch
        {
            SignedXml.XmlDsigSHA256Url => [System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256],
            SignedXml.XmlDsigSHA384Url => [System.Security.Cryptography.RSAEncryptionPadding.OaepSHA384],
            SignedXml.XmlDsigSHA512Url => [System.Security.Cryptography.RSAEncryptionPadding.OaepSHA512],
            SignedXml.XmlDsigSHA1Url => [System.Security.Cryptography.RSAEncryptionPadding.OaepSHA1],
            // No DigestMethod. mgf1p implies SHA-1; xenc11 without one is under-specified, so prefer
            // SHA-256 and fall back to SHA-1 for interoperability with older IdPs.
            _ => defaultToSha1
                ? [System.Security.Cryptography.RSAEncryptionPadding.OaepSHA1,
                   System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256]
                : [System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256,
                   System.Security.Cryptography.RSAEncryptionPadding.OaepSHA1],
        };
    }

    public SamlParseResult Parse(string base64Response, SamlResponseValidationContext context)
    {
        // 1. Base64 decode
        byte[] responseBytes;
        try
        {
            responseBytes = Convert.FromBase64String(base64Response);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "SAML response is not valid Base64");
            return Fail("Invalid Base64 encoding in SAML response.");
        }

        // 2. Load into XmlDocument (PreserveWhitespace = true is critical for signature validation).
        // DTDs are prohibited to block XXE and internal entity-expansion DoS on this attacker-supplied XML.
        XmlDocument doc;
        try
        {
            doc = LoadHardened(System.Text.Encoding.UTF8.GetString(responseBytes));
        }
        catch (XmlException ex)
        {
            logger.LogWarning(ex, "SAML response is not valid XML");
            return Fail("Invalid XML in SAML response.");
        }

        // 3. Create namespace manager
        var nsManager = new XmlNamespaceManager(doc.NameTable);
        nsManager.AddNamespace("samlp", SamlConstants.Saml2Protocol);
        nsManager.AddNamespace("saml", SamlConstants.Saml2Assertion);
        nsManager.AddNamespace("ds", SamlConstants.XmlDSig);

        // Get the Response element
        var responseElement = doc.DocumentElement;
        if (responseElement is null || responseElement.LocalName != "Response")
            return Fail("Root element is not a SAML Response.");

        // 4. Validate Status
        var statusCodeNode = responseElement.SelectSingleNode(
            "samlp:Status/samlp:StatusCode", nsManager);
        var statusValue = statusCodeNode?.Attributes?["Value"]?.Value;
        if (!string.Equals(statusValue, SamlConstants.StatusSuccess, StringComparison.Ordinal))
        {
            logger.LogWarning("SAML response status: {Status}", statusValue);
            return Fail($"SAML response status is not Success: {statusValue}");
        }

        // Set from the SIGNED assertion further down; declared here so it can reach the result.
        string? signedInResponseTo = null;

        // 5. Validate InResponseTo
        if (context.ExpectedInResponseTo is not null)
        {
            var inResponseTo = responseElement.Attributes?["InResponseTo"]?.Value;
            if (!string.Equals(inResponseTo, context.ExpectedInResponseTo, StringComparison.Ordinal))
            {
                logger.LogWarning("InResponseTo mismatch: expected={Expected}, actual={Actual}",
                    context.ExpectedInResponseTo, inResponseTo);
                return Fail("InResponseTo does not match the expected request ID.");
            }
        }

        // 6. Validate Destination
        var destination = responseElement.Attributes?["Destination"]?.Value;
        if (!string.IsNullOrEmpty(destination) &&
            !string.Equals(destination, context.ExpectedAcsUrl, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Destination mismatch: expected={Expected}, actual={Actual}",
                context.ExpectedAcsUrl, destination);
            return Fail("Response Destination does not match the expected ACS URL.");
        }

        // 7. Signature Validation (handle all Azure AD variations)
        // F54: an EncryptedAssertion (ADFS default once the SP advertises an encryption cert) is
        // decrypted with the SP private key first; the decrypted element then goes through the
        // exact same signature/conditions pipeline as a plaintext assertion.
        var assertionNode = responseElement.SelectSingleNode("saml:Assertion", nsManager);
        if (assertionNode is null)
        {
            var encryptedNode = responseElement.SelectSingleNode("saml:EncryptedAssertion", nsManager);
            if (encryptedNode is XmlElement encryptedElement)
            {
                if (context.DecryptionKey is null)
                    return Fail("SAML response carries an EncryptedAssertion but this connection has no SP decryption key. Disable assertion encryption at the IdP or recreate the connection.");
                try
                {
                    DecryptAssertion(encryptedElement, context.DecryptionKey, doc);
                }
                catch (Exception ex)
                {
                    // ONE constant message for every decryption failure, whatever the cause. Reflecting
                    // ex.Message returned the exact CryptographicException text to an anonymous caller,
                    // which is the distinguishing signal a Bleichenbacher (RSA key transport) or CBC
                    // padding-oracle attack needs — it turns ~10^4-10^5 probes into a plaintext recovery
                    // or a signature forgery. Detail goes to the log only.
                    logger.LogWarning(ex, "Failed to decrypt EncryptedAssertion");
                    return Fail(DecryptionFailure);
                }
                assertionNode = responseElement.SelectSingleNode("saml:EncryptedAssertion/saml:Assertion", nsManager)
                    ?? responseElement.SelectSingleNode("saml:Assertion", nsManager);
            }
        }
        if (assertionNode is not XmlElement assertionElement)
            return Fail("SAML response does not contain an Assertion.");

        var assertionId = assertionElement.Attributes?["ID"]?.Value;

        var responseSignatureValid = ValidateElementSignature(
            responseElement, context.TrustedCertificates, logger);
        var assertionSignatureValid = ValidateElementSignature(
            assertionElement, context.TrustedCertificates, logger);

        if (!responseSignatureValid && !assertionSignatureValid)
        {
            logger.LogWarning("No valid signature found on Response or Assertion");
            return Fail(SignatureFailure);
        }

        // 8. Validate Assertion Conditions — required, and must bind the audience to this SP so an
        // assertion minted for a different audience can't be replayed here. Fail closed if absent.
        var conditionsNode = assertionElement.SelectSingleNode("saml:Conditions", nsManager);
        if (conditionsNode is not XmlElement conditionsElement)
            return Fail("Assertion is missing the required Conditions element.");

        var now = DateTimeOffset.UtcNow;

        var notBeforeStr = conditionsElement.Attributes?["NotBefore"]?.Value;
        if (notBeforeStr is not null && DateTimeOffset.TryParse(notBeforeStr, out var notBefore))
        {
            if (now + context.ClockSkew < notBefore)
            {
                logger.LogWarning("Assertion not yet valid: NotBefore={NotBefore}, Now={Now}", notBefore, now);
                return Fail("Assertion is not yet valid (NotBefore condition).");
            }
        }

        var notOnOrAfterStr = conditionsElement.Attributes?["NotOnOrAfter"]?.Value;
        if (notOnOrAfterStr is not null && DateTimeOffset.TryParse(notOnOrAfterStr, out var notOnOrAfter))
        {
            if (now - context.ClockSkew >= notOnOrAfter)
            {
                logger.LogWarning("Assertion expired: NotOnOrAfter={NotOnOrAfter}, Now={Now}", notOnOrAfter, now);
                return Fail("Assertion has expired (NotOnOrAfter condition).");
            }
        }

        var audienceNode = conditionsElement.SelectSingleNode(
            "saml:AudienceRestriction/saml:Audience", nsManager);
        var audience = audienceNode?.InnerText?.Trim();
        if (string.IsNullOrEmpty(audience))
            return Fail("Assertion is missing the required AudienceRestriction/Audience.");
        if (!string.Equals(audience, context.ExpectedAudience, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Audience mismatch: expected={Expected}, actual={Actual}",
                context.ExpectedAudience, audience);
            return Fail("Assertion audience does not match the expected audience.");
        }

        // 9. Validate SubjectConfirmation. REQUIRED, not conditional — SAML 2.0 Profiles §4.1.4.2/§4.1.4.3
        // (with errata E52/E26) require at least one bearer <SubjectConfirmation> whose
        // <SubjectConfirmationData> carries a Recipient matching this ACS and a NotOnOrAfter bounding
        // confirmation, and require the SP to verify each.
        //
        // Every part of this used to be enforced only "if present", so an assertion with no
        // SubjectConfirmation at all, or with one carrying no SubjectConfirmationData, was accepted. That
        // mattered beyond conformance: SubjectConfirmationData/NotOnOrAfter is the SHORT bound (minutes at
        // Entra/Okta/Google) while Conditions/NotOnOrAfter is the long one (~an hour), so with the short
        // bound absent an assertion stayed acceptable far longer than its issuer intended — long enough to
        // outlive the replay cache's retention and be replayed. Fixing the fail-open is what closes that
        // compound issue; the retention bound below is the second half.
        //
        // NOTE: only the FIRST SubjectConfirmation is examined, so "at least one bearer confirmation" is
        // really "the first one must be bearer". That is stricter than the spec, not looser.
        if (assertionElement.SelectSingleNode("saml:Subject/saml:SubjectConfirmation", nsManager)
            is not XmlElement subjectConfirmation)
            return Fail("Assertion is missing the required Subject/SubjectConfirmation element.");

        var method = subjectConfirmation.Attributes?["Method"]?.Value;
        if (!string.Equals(method, SamlConstants.BearerConfirmation, StringComparison.Ordinal))
        {
            logger.LogWarning("Unsupported SubjectConfirmation method: {Method}", method);
            return Fail($"Unsupported SubjectConfirmation method: {method}");
        }

        if (subjectConfirmation.SelectSingleNode("saml:SubjectConfirmationData", nsManager)
            is not XmlElement confirmationData)
            return Fail("Assertion is missing the required SubjectConfirmationData element.");

        var recipient = confirmationData.Attributes?["Recipient"]?.Value;
        if (string.IsNullOrEmpty(recipient))
            return Fail("SubjectConfirmationData is missing the required Recipient attribute.");
        if (!string.Equals(recipient, context.ExpectedAcsUrl, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Recipient mismatch: expected={Expected}, actual={Actual}",
                context.ExpectedAcsUrl, recipient);
            return Fail("SubjectConfirmationData Recipient does not match the expected ACS URL.");
        }

        // The confirmation window. Required, and unparseable is a failure rather than "skip the check" —
        // fail-open on a malformed timestamp is the same defect as fail-open on a missing one.
        var dataNotOnOrAfterStr = confirmationData.Attributes?["NotOnOrAfter"]?.Value;
        if (string.IsNullOrEmpty(dataNotOnOrAfterStr))
            return Fail("SubjectConfirmationData is missing the required NotOnOrAfter attribute.");
        if (!DateTimeOffset.TryParse(dataNotOnOrAfterStr, out var dataNotOnOrAfter))
            return Fail("SubjectConfirmationData NotOnOrAfter is not a valid timestamp.");
        if (now - context.ClockSkew >= dataNotOnOrAfter)
        {
            logger.LogWarning("SubjectConfirmationData expired: NotOnOrAfter={NotOnOrAfter}, Now={Now}",
                dataNotOnOrAfter, now);
            return Fail("SubjectConfirmationData has expired.");
        }

        signedInResponseTo = confirmationData.Attributes?["InResponseTo"]?.Value;
        var dataInResponseTo = signedInResponseTo;
        if (context.ExpectedInResponseTo is not null && dataInResponseTo is not null &&
            !string.Equals(dataInResponseTo, context.ExpectedInResponseTo, StringComparison.Ordinal))
        {
            logger.LogWarning("SubjectConfirmationData InResponseTo mismatch");
            return Fail("SubjectConfirmationData InResponseTo does not match.");
        }

        // How long this assertion remains acceptable to US. The replay record must be retained at least
        // this long (SAML 2.0 Profiles §4.1.4.5), otherwise the id is forgotten while the assertion is
        // still valid and can be presented again.
        var acceptableUntil = dataNotOnOrAfter + context.ClockSkew;

        // 10. Extract NameID
        var nameIdNode = assertionElement.SelectSingleNode("saml:Subject/saml:NameID", nsManager);
        var nameId = nameIdNode?.InnerText?.Trim();
        var nameIdFormat = (nameIdNode as XmlElement)?.Attributes?["Format"]?.Value;

        if (string.IsNullOrEmpty(nameId))
            return Fail("Assertion does not contain a NameID.");

        // 11. Extract Attributes — every AttributeValue (multi-valued attributes like groups carry
        // one element per entry), indexed under both Name and FriendlyName (Okta/Shibboleth emit
        // OID Names with human FriendlyNames; matching either is what makes vendor mapping work).
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attributeValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var attributeNodes = assertionElement.SelectNodes(
            "saml:AttributeStatement/saml:Attribute", nsManager);
        if (attributeNodes is not null)
        {
            foreach (XmlElement attrElement in attributeNodes)
            {
                var attrName = attrElement.Attributes?["Name"]?.Value;
                if (string.IsNullOrEmpty(attrName))
                    continue;

                var values = new List<string>();
                var valueNodes = attrElement.SelectNodes("saml:AttributeValue", nsManager);
                if (valueNodes is not null)
                {
                    foreach (System.Xml.XmlNode valueNode in valueNodes)
                    {
                        var v = valueNode.InnerText?.Trim();
                        if (!string.IsNullOrEmpty(v))
                            values.Add(v);
                    }
                }
                if (values.Count == 0)
                    continue;

                var friendlyName = attrElement.Attributes?["FriendlyName"]?.Value;
                foreach (var key in new[] { attrName, friendlyName })
                {
                    if (string.IsNullOrEmpty(key))
                        continue;
                    attributes.TryAdd(key, values[0]);
                    if (!attributeValues.TryGetValue(key, out var existing))
                        attributeValues[key] = [.. values];
                    else
                        existing.AddRange(values);
                }
            }
        }

        // 12. Extract SessionIndex
        var authnStatementNode = assertionElement.SelectSingleNode(
            "saml:AuthnStatement", nsManager) as XmlElement;
        var sessionIndex = authnStatementNode?.Attributes?["SessionIndex"]?.Value;

        logger.LogInformation("SAML response parsed successfully. NameID={NameId}, Attributes={Count}",
            nameId, attributes.Count);

        return new SamlParseResult
        {
            Success = true,
            NameId = nameId,
            NameIdFormat = nameIdFormat,
            Attributes = attributes,
            AttributeValues = attributeValues,
            SessionIndex = sessionIndex,
            AssertionId = assertionId,
            AcceptableUntil = acceptableUntil,
            SignedInResponseTo = signedInResponseTo,
        };
    }

    /// <summary>
    /// The only transforms a signature reference may declare. Anything else changes what gets digested
    /// (or, for XSLT, executes during verification), which defeats the reference-URI check.
    /// </summary>
    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigEnvelopedSignatureTransformUrl,
        SignedXml.XmlDsigC14NTransformUrl,
        SignedXml.XmlDsigC14NWithCommentsTransformUrl,
        SignedXml.XmlDsigExcC14NTransformUrl,
        SignedXml.XmlDsigExcC14NWithCommentsTransformUrl,
    };

    private static readonly HashSet<string> AllowedCanonicalizationMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigC14NTransformUrl,
        SignedXml.XmlDsigC14NWithCommentsTransformUrl,
        SignedXml.XmlDsigExcC14NTransformUrl,
        SignedXml.XmlDsigExcC14NWithCommentsTransformUrl,
    };

    /// <summary>
    /// Signature algorithms we will verify. Public-key only, by design: an HMAC SignatureMethod invites
    /// the key-confusion attack where the verifier is talked into treating the IdP's *public* certificate
    /// as a shared secret, which the attacker also has. SHA-1 stays for legacy ADFS reach — a documented
    /// trade, and separate from the algorithm-confusion class this list closes.
    /// </summary>
    private static readonly HashSet<string> AllowedSignatureMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigRSASHA1Url,
        SignedXml.XmlDsigRSASHA256Url,
        SignedXml.XmlDsigRSASHA384Url,
        SignedXml.XmlDsigRSASHA512Url,
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256",
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha384",
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512",
    };

    /// <summary>Digest algorithms we will accept. MD5 and anything unrecognised are refused.</summary>
    private static readonly HashSet<string> AllowedDigestMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigSHA1Url,
        SignedXml.XmlDsigSHA256Url,
        SignedXml.XmlDsigSHA384Url,
        SignedXml.XmlDsigSHA512Url,
    };

    /// <summary>
    /// True when any ID value appears on more than one element. Checks the three attribute spellings
    /// .NET's reference resolver looks at, not just SAML's "ID", so the check covers everything
    /// <c>GetIdElement</c> could latch onto.
    /// </summary>
    private static bool HasDuplicateIds(XmlDocument doc, out string duplicateId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlNode node in doc.SelectNodes("//*")!)
        {
            if (node is not XmlElement el || el.Attributes is null) continue;
            foreach (var name in new[] { "ID", "Id", "id" })
            {
                var value = el.Attributes[name]?.Value;
                if (string.IsNullOrEmpty(value)) continue;
                if (!seen.Add(value))
                {
                    duplicateId = value;
                    return true;
                }
            }
        }
        duplicateId = "";
        return false;
    }

    // Public: the SLO endpoint reuses this to validate POST-binding LogoutRequest signatures.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "SAML XML signature validation requires reflection")]
    public static bool ValidateElementSignature(
        XmlElement element,
        IReadOnlyList<X509Certificate2> trustedCertificates,
        ILogger logger)
    {
        // Find <ds:Signature> that is a direct child of the target element
        XmlElement? signatureElement = null;
        foreach (XmlNode child in element.ChildNodes)
        {
            if (child is XmlElement el &&
                el.LocalName == "Signature" &&
                el.NamespaceURI == SamlConstants.XmlDSig)
            {
                signatureElement = el;
                break;
            }
        }

        if (signatureElement is null)
            return false; // No signature on this element — not an error, the other element might be signed

        // SECURITY: a duplicated ID makes "which element does #id mean?" ambiguous. We compare the
        // Reference URI to THIS element's ID below, but CheckSignature resolves #id itself, across the
        // whole document, first match wins — so with two elements sharing an ID those two resolutions
        // can disagree: sign one element, read another. Rejecting duplicates removes the ambiguity
        // rather than relying on both lookups happening to choose the same node.
        if (element.OwnerDocument is { } ownerDoc && HasDuplicateIds(ownerDoc, out var duplicateId))
        {
            logger.LogWarning("SAML document contains a duplicate ID '{Id}' — refusing to validate", duplicateId);
            return false;
        }

        var signedXml = new SignedXml(element);
        signedXml.LoadXml(signatureElement);

        // SECURITY: Verify the Reference URI matches the signed element's ID
        // This prevents signature wrapping attacks
        if (signedXml.SignedInfo?.References is { Count: > 0 })
        {
            // Exactly one reference. No IdP emits more, and validating the first while letting the rest
            // through unchecked is the sort of gap wrapping attacks are built out of.
            if (signedXml.SignedInfo.References.Count != 1)
            {
                logger.LogWarning("Signature carries {Count} references; exactly one is expected",
                    signedXml.SignedInfo.References.Count);
                return false;
            }

            var reference = (Reference)signedXml.SignedInfo.References[0]!;
            var referenceUri = reference.Uri;

            // Allowlist the transform chain. Transforms decide WHICH BYTES get digested, so an
            // unrestricted chain is what would undo the URI check below: an XPath transform can aim the
            // digest at content other than the element the URI names, and XmlDsigXsltTransform would run
            // attacker-supplied XSLT inside the verifier.
            // MEASURED: .NET already refuses the XSLT and XPath chains we could construct, so this is
            // policy made explicit rather than a hole being closed. It stops the guarantee resting on
            // runtime behaviour we do not control, and fails closed if a future runtime relaxes.
            // Enveloped-signature plus canonicalization is everything a real IdP sends.
            foreach (Transform transform in reference.TransformChain)
            {
                if (!AllowedTransforms.Contains(transform.Algorithm ?? ""))
                {
                    logger.LogWarning("Signature uses a disallowed transform: {Algorithm}", transform.Algorithm);
                    return false;
                }
            }

            // Algorithm confusion: pin the signature and digest algorithms to public-key primitives we
            // actually intend to accept, rather than verifying whatever the document asks for. An HMAC
            // SignatureMethod is the case that matters — it invites a verifier into treating the IdP's
            // public certificate as a shared secret the attacker also holds.
            if (!AllowedSignatureMethods.Contains(signedXml.SignedInfo.SignatureMethod ?? ""))
            {
                logger.LogWarning("Signature uses a disallowed signature method: {Method}",
                    signedXml.SignedInfo.SignatureMethod);
                return false;
            }

            if (!AllowedDigestMethods.Contains(reference.DigestMethod ?? ""))
            {
                logger.LogWarning("Signature reference uses a disallowed digest method: {Method}",
                    reference.DigestMethod);
                return false;
            }

            if (!AllowedCanonicalizationMethods.Contains(signedXml.SignedInfo.CanonicalizationMethod ?? ""))
            {
                logger.LogWarning("Signature uses a disallowed canonicalization method: {Method}",
                    signedXml.SignedInfo.CanonicalizationMethod);
                return false;
            }

            // The URI should be #ID where ID matches the element's ID attribute
            var elementId = element.Attributes?["ID"]?.Value;
            if (elementId is null)
            {
                logger.LogWarning("Signed element has no ID attribute");
                return false;
            }

            var expectedUri = $"#{elementId}";
            if (!string.Equals(referenceUri, expectedUri, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Signature Reference URI mismatch: expected={Expected}, actual={Actual}",
                    expectedUri, referenceUri);
                return false;
            }
        }
        else
        {
            logger.LogWarning("Signature has no references");
            return false;
        }

        // Try each trusted certificate. Verify against the certificate's explicitly
        // extracted public key — NOT SignedXml.CheckSignature(X509Certificate2, ...), whose
        // overload routes through the legacy X509Certificate2.PublicKey.Key accessor and
        // throws NullReferenceException for RSA-SHA256 signatures on .NET/Linux (which is
        // exactly what Entra emits). CheckSignature(AsymmetricAlgorithm) verifies the
        // signature only (no chain) — trust is already established by pinning the IdP's
        // metadata signing certificates.
        foreach (var cert in trustedCertificates)
        {
            try
            {
                using var publicKey = (System.Security.Cryptography.AsymmetricAlgorithm?)cert.GetRSAPublicKey()
                                      ?? cert.GetECDsaPublicKey();
                if (publicKey is not null && signedXml.CheckSignature(publicKey))
                {
                    logger.LogDebug("Signature validated against certificate: {Thumbprint}",
                        cert.Thumbprint);
                    return true;
                }
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // Certificate algorithm mismatch or inapplicable cert — try next
                logger.LogDebug(ex, "Signature check inapplicable for certificate {Thumbprint}",
                    cert.Thumbprint);
            }
            catch (Exception ex)
            {
                // Unexpected error — log as warning since it may indicate a real problem
                logger.LogWarning(ex, "Unexpected error during signature check with certificate {Thumbprint}",
                    cert.Thumbprint);
            }
        }

        logger.LogWarning("Signature could not be validated against any trusted certificate");
        return false;
    }

    private static SamlParseResult Fail(string error) => new() { Success = false, Error = error };

    /// <summary>
    /// F54: decrypt an xenc EncryptedAssertion in place. Handles the shapes real IdPs emit: an
    /// EncryptedKey either referenced from the EncryptedData's KeyInfo or as a sibling under the
    /// EncryptedAssertion; RSA-OAEP(SHA1/SHA256) or RSA-1.5 key transport; AES-CBC / 3DES data
    /// encryption (AES-GCM is not supported by <see cref="EncryptedXml"/> — a clear error beats a
    /// silent failure).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "SAML XML decryption requires reflection")]
    private static void DecryptAssertion(XmlElement encryptedAssertion, System.Security.Cryptography.RSA decryptionKey, XmlDocument doc)
    {
        var nsManager = new XmlNamespaceManager(doc.NameTable);
        nsManager.AddNamespace("xenc", EncryptedXml.XmlEncNamespaceUrl);

        if (encryptedAssertion.SelectSingleNode(".//xenc:EncryptedData", nsManager) is not XmlElement encryptedDataElement)
            throw new InvalidOperationException("EncryptedAssertion has no EncryptedData element.");

        var encryptedData = new EncryptedData();
        encryptedData.LoadXml(encryptedDataElement);

        // Find the EncryptedKey: inside the EncryptedData KeyInfo, or anywhere under the EncryptedAssertion.
        if (encryptedAssertion.SelectSingleNode(".//xenc:EncryptedKey", nsManager) is not XmlElement encryptedKeyElement)
            throw new InvalidOperationException("EncryptedAssertion has no EncryptedKey element.");
        var encryptedKey = new EncryptedKey();
        encryptedKey.LoadXml(encryptedKeyElement);

        var wrappedKey = encryptedKey.CipherData?.CipherValue
            ?? throw new InvalidOperationException("EncryptedKey has no CipherValue.");

        // Key transport. RSA-PKCS#1 v1.5 is REFUSED: XML Encryption 1.1 §5.5.1 deprecates it and the OASIS
        // SAML V2.0 Implementation Profile for Encryption requires RSA-OAEP, precisely because v1.5
        // unwrapping is a Bleichenbacher/ROBOT decryption oracle against the SP private key. The ACS is
        // anonymous and unauthenticated, and the SP keypair is minted for every connection whether or not
        // the IdP encrypts, so accepting v1.5 armed that oracle by default on every connection.
        //
        // The declared algorithm is honoured rather than trying paddings in turn: a trial loop is itself an
        // oracle, because which padding "succeeded" is attacker-observable through timing and behaviour.
        var keyAlgorithm = encryptedKey.EncryptionMethod?.KeyAlgorithm ?? "";
        var paddings = keyAlgorithm switch
        {
            // xmlenc#rsa-oaep-mgf1p: MGF1-SHA1 with the digest given by an optional DigestMethod child.
            EncryptedXml.XmlEncRSAOAEPUrl => OaepPaddingsFor(encryptedKeyElement, doc, defaultToSha1: true),
            // xenc11#rsa-oaep: digest declared explicitly.
            Xmlenc11RsaOaepUrl => OaepPaddingsFor(encryptedKeyElement, doc, defaultToSha1: false),
            _ => throw new InvalidOperationException(DecryptionFailure),
        };

        byte[]? contentKey = null;
        foreach (var padding in paddings)
        {
            try
            {
                contentKey = decryptionKey.Decrypt(wrappedKey, padding);
                break;
            }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        if (contentKey is null)
            throw new InvalidOperationException(DecryptionFailure);

        var dataAlgorithm = encryptedData.EncryptionMethod?.KeyAlgorithm ?? "";
        System.Security.Cryptography.SymmetricAlgorithm symmetric = dataAlgorithm switch
        {
            EncryptedXml.XmlEncAES128Url or EncryptedXml.XmlEncAES192Url or EncryptedXml.XmlEncAES256Url
                => System.Security.Cryptography.Aes.Create(),
            EncryptedXml.XmlEncTripleDESUrl => System.Security.Cryptography.TripleDES.Create(),
            // Constant message: naming the rejected algorithm told an attacker which of their probes was
            // structurally wrong versus cryptographically wrong.
            _ => throw new InvalidOperationException(DecryptionFailure),
        };
        try
        {
            symmetric.Key = contentKey;
            var encryptedXml = new EncryptedXml(doc);
            var plaintext = encryptedXml.DecryptData(encryptedData, symmetric);
            encryptedXml.ReplaceData(encryptedDataElement, plaintext);
        }
        finally
        {
            symmetric.Dispose();
            System.Array.Clear(contentKey);
        }
    }
}
