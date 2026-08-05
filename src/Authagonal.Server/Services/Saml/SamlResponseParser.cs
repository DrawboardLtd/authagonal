using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Authagonal.Server.Services.Saml;

public sealed record SamlResponseValidationContext(
    string ExpectedAcsUrl,
    string ExpectedAudience,
    string? ExpectedInResponseTo,
    IReadOnlyList<X509Certificate2> TrustedCertificates,
    /// <summary>
    /// The IdP entityID this connection is configured for. When set, the Response's and Assertion's
    /// <c>Issuer</c> must equal it.
    /// </summary>
    /// <remarks>
    /// The Issuer was never read at all. Trust rested entirely on the signing certificate, which is
    /// enough only while one certificate maps to one issuer — but a deployment whose IdPs share a
    /// CA-issued cert, or whose metadata for two connections resolves to overlapping keys, had no
    /// binding between "this signature verifies" and "this is the IdP this connection means". SAML
    /// 2.0 Core §3.2.2 makes Issuer the identifier of the asserting party; checking it is what turns
    /// a valid signature into a valid signature FROM THE RIGHT PARTY.
    /// </remarks>
    string? ExpectedIssuer = null,
    TimeSpan ClockSkew = default,
    System.Security.Cryptography.RSA? DecryptionKey = null,
    /// <summary>
    /// How far past its own <c>IssueInstant</c> an assertion may still be presented here, whatever the
    /// IdP's <c>NotOnOrAfter</c> attributes say. One hour by default.
    /// </summary>
    /// <remarks>
    /// Web-SSO assertions are consumed within seconds of being minted: the browser POSTs them straight
    /// from the IdP to this ACS. The cap exists for the case where the issuer's own bounds are not
    /// trustworthy — a compromised IdP, or one misconfigured to a month-long window — where without it a
    /// captured assertion stays replayable for exactly as long as its issuer decided, and the replay
    /// cache has to retain the assertion id for the same span or forget it while it is still valid.
    /// </remarks>
    TimeSpan MaxAssertionAge = default)
{
    public TimeSpan ClockSkew { get; init; } = ClockSkew == default ? TimeSpan.FromMinutes(5) : ClockSkew;

    public TimeSpan MaxAssertionAge { get; init; } =
        MaxAssertionAge == default ? TimeSpan.FromHours(1) : MaxAssertionAge;
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
    /// <c>AuthnStatement/@SessionNotOnOrAfter</c> — the IdP's own upper bound on the session it just
    /// asserted. Null when the IdP states none.
    /// </summary>
    /// <remarks>
    /// Read by nothing before, so a local session outlived the authentication behind it: an IdP
    /// saying "this session is good for eight hours" was overruled by whatever the local cookie
    /// lifetime happened to be — the opposite of what federating to that IdP means, and the reason
    /// deprovisioning at the IdP did not take effect here.
    /// </remarks>
    public DateTimeOffset? SessionNotOnOrAfter { get; init; }

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
    /// <summary>
    /// Parses an xs:dateTime from a SAML document, culture-independently.
    /// </summary>
    /// <remarks>
    /// <c>DateTimeOffset.TryParse</c> without an explicit culture honours the ambient one, so the
    /// same assertion could parse differently on two pods configured with different locales. XML
    /// timestamps are defined in a single format; this reads that format and nothing else.
    /// </remarks>
    internal static bool TryParseSamlInstant(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal,
            out instant);

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

        // Core §3.2.2 makes Version REQUIRED and fixes it at "2.0"; §4.1 obliges a responder that
        // cannot process the version to say so rather than proceeding. Parsing an unversioned or
        // future-versioned document with 2.0 semantics is how a element that means something else
        // gets read as one this parser understands.
        if (!string.Equals(responseElement.GetAttribute("Version"), "2.0", StringComparison.Ordinal))
            return Fail("SAML Response Version is not 2.0.");

        // Issuer, before anything is read out of the document.
        if (!string.IsNullOrEmpty(context.ExpectedIssuer))
        {
            var responseIssuer = responseElement.SelectSingleNode("saml:Issuer", nsManager)?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(responseIssuer) &&
                !string.Equals(responseIssuer, context.ExpectedIssuer, StringComparison.Ordinal))
            {
                logger.LogWarning("SAML Response Issuer mismatch: expected={Expected}, actual={Actual}",
                    context.ExpectedIssuer, responseIssuer);
                return Fail("SAML Response Issuer does not match this connection's IdP.");
            }
        }

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
        //
        // Required whenever the Response itself is signed — SAML 2.0 Bindings §3.5.5.2 for HTTP-POST:
        // "If the message is signed, the Destination XML attribute in the root SAML element MUST be
        // present." Core §3.2.2 then obliges the recipient to compare it. Only comparing "if present"
        // let an attacker delete the attribute and skip the comparison, which is the anti-forwarding
        // control the attribute exists to provide — and Destination sits OUTSIDE the assertion, so on
        // the common assertion-only-signed shape it can be deleted without breaking anything.
        //
        // The requirement is therefore tied to the Response signature, which is what covers the
        // attribute: strip that signature and the requirement lifts, but then the signed, mandatory
        // SubjectConfirmationData/@Recipient below is the binding to this ACS and it cannot be stripped.
        var destination = responseElement.Attributes?["Destination"]?.Value;
        var responseIsSigned = responseElement.SelectSingleNode("ds:Signature", nsManager) is not null;
        if (responseIsSigned && string.IsNullOrEmpty(destination))
        {
            logger.LogWarning("Signed SAML Response carries no Destination attribute");
            return Fail("A signed SAML Response must carry the Destination attribute.");
        }
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
        // Set when the assertion is encrypted: the Response-level signature verified against the
        // document as the IdP signed it, BEFORE decryption rewrites it.
        bool? preDecryptResponseSignatureValid = null;

        var assertionNode = responseElement.SelectSingleNode("saml:Assertion", nsManager);
        if (assertionNode is null)
        {
            var encryptedNode = responseElement.SelectSingleNode("saml:EncryptedAssertion", nsManager);
            if (encryptedNode is XmlElement encryptedElement)
            {
                if (context.DecryptionKey is null)
                    return Fail("SAML response carries an EncryptedAssertion but this connection has no SP decryption key. Disable assertion encryption at the IdP or recreate the connection.");

                // Decryption calls EncryptedXml.ReplaceData, which mutates the loaded document in
                // place — <xenc:EncryptedData> becomes the plaintext <saml:Assertion>. The Response
                // signature was computed over the document CONTAINING the encrypted blob, so
                // recomputing its reference digest afterwards can never match: responseSignatureValid
                // was unconditionally false for every encrypted response, and only a signature applied
                // to the assertion before encryption could ever succeed. An IdP that signs the
                // Response and encrypts the assertion — a supported combination — could not federate
                // at all, and the failure looked like a signature problem rather than an ordering one.
                //
                // Verified on a pristine copy so the working document is still free to be mutated.
                try
                {
                    var pristine = LoadHardened(doc.OuterXml);
                    if (pristine.DocumentElement is { } pristineResponse)
                    {
                        preDecryptResponseSignatureValid =
                            ValidateElementSignature(pristineResponse, context.TrustedCertificates, logger);
                    }
                }
                catch (Exception ex)
                {
                    // A copy that will not reload cannot be verified; treat it as unsigned and let the
                    // assertion-level signature decide, exactly as before.
                    logger.LogWarning(ex, "Could not verify the Response signature before decryption");
                    preDecryptResponseSignatureValid = false;
                }

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

        // Core §2.3.3 — same requirement on the Assertion, checked separately because a decrypted
        // EncryptedAssertion carries its own Version that the Response's says nothing about.
        if (!string.Equals(assertionElement.GetAttribute("Version"), "2.0", StringComparison.Ordinal))
            return Fail("SAML Assertion Version is not 2.0.");

        if (!string.IsNullOrEmpty(context.ExpectedIssuer))
        {
            // The assertion's own Issuer is inside the signature, so this is the binding that counts.
            var assertionIssuer = assertionElement.SelectSingleNode("saml:Issuer", nsManager)?.InnerText?.Trim();
            if (!string.Equals(assertionIssuer, context.ExpectedIssuer, StringComparison.Ordinal))
            {
                logger.LogWarning("SAML Assertion Issuer mismatch: expected={Expected}, actual={Actual}",
                    context.ExpectedIssuer, assertionIssuer);
                return Fail("SAML Assertion Issuer does not match this connection's IdP.");
            }
        }

        var assertionId = assertionElement.Attributes?["ID"]?.Value;

        // For an encrypted assertion this was decided above, against the unmutated document.
        var responseSignatureValid = preDecryptResponseSignatureValid
            ?? ValidateElementSignature(responseElement, context.TrustedCertificates, logger);
        var assertionSignatureValid = ValidateElementSignature(
            assertionElement, context.TrustedCertificates, logger);

        if (!responseSignatureValid && !assertionSignatureValid)
        {
            logger.LogWarning("No valid signature found on Response or Assertion");
            return Fail(SignatureFailure);
        }

        // Destination is only OPTIONAL on an unsigned message. Core §3.2.2 makes it mandatory once the
        // message is signed, and for a reason the compare-if-present check at step 6 cannot supply: the
        // attribute is the SP's only evidence that this Response was addressed HERE. A signed Response
        // with no Destination is one the IdP minted for whoever holds it, so it can be forwarded from
        // the SP it was meant for to this one and still verify. Deferred to after signature validation
        // because the requirement attaches to the signature, not to the message.
        // The Destination requirement is NOT repeated here, and deliberately not.
        //
        // Two branches closed #260 and #6 independently and both added this check; the merge kept both,
        // which left this one dead. Reaching it with responseSignatureValid == true means a ds:Signature
        // element was present on the Response, and the check above already refused that shape when
        // Destination was empty — so the condition here can never be true.
        //
        // The earlier one is the one to keep: it keys on the signature being PRESENT rather than valid, so
        // it also refuses a Response that purports to be signed and is not, and Bindings §3.5.5.2 attaches
        // the requirement to the message being signed rather than to the signature verifying. Do not
        // re-add a post-validation copy; it would be unreachable again.
        var now = DateTimeOffset.UtcNow;

        // 7b. Absolute age. IssueInstant is REQUIRED on an Assertion (Core §2.3.3) and was read by
        // nothing, so the only bound on how old an assertion could be was whatever the IdP put in its
        // own NotOnOrAfter attributes. That inverts who decides: a compromised or misconfigured IdP
        // emitting a month-long window made a single captured assertion good for a month here, and it
        // forced the replay cache to retain the id for that whole month or forget it while it was still
        // acceptable. The cap is this SP's own statement of how long a web-SSO assertion can be worth
        // presenting — seconds, in a live flow — so it never trips a real IdP but does bound a bad one.
        var issueInstantStr = assertionElement.Attributes?["IssueInstant"]?.Value;
        if (string.IsNullOrEmpty(issueInstantStr))
            return Fail("Assertion is missing the required IssueInstant attribute.");
        if (!TryParseSamlInstant(issueInstantStr, out var issueInstant))
            return Fail("Assertion IssueInstant is not a valid timestamp.");
        if (now - issueInstant > context.MaxAssertionAge + context.ClockSkew)
        {
            logger.LogWarning("Assertion is older than the accepted maximum: IssueInstant={IssueInstant}, Now={Now}",
                issueInstant, now);
            return Fail("Assertion is older than the accepted maximum age.");
        }
        if (issueInstant - now > context.ClockSkew)
        {
            logger.LogWarning("Assertion IssueInstant is in the future: IssueInstant={IssueInstant}, Now={Now}",
                issueInstant, now);
            return Fail("Assertion IssueInstant is in the future.");
        }

        // 8. Validate Assertion Conditions — required, and must bind the audience to this SP so an
        // assertion minted for a different audience can't be replayed here. Fail closed if absent.
        var conditionsNode = assertionElement.SelectSingleNode("saml:Conditions", nsManager);
        if (conditionsNode is not XmlElement conditionsElement)
            return Fail("Assertion is missing the required Conditions element.");

        // A timestamp that is PRESENT but unparseable now fails closed.
        //
        // `TryParse(...) &&` meant a malformed value skipped the comparison entirely — so an
        // attacker who could influence the assertion's conditions (or an IdP emitting a format this
        // parser does not read) removed the validity window rather than failing validation, and the
        // assertion became acceptable forever. Parsing is pinned to the invariant culture with
        // round-trip/universal styles too: xs:dateTime is culture-independent, but TryParse without
        // them honours the ambient culture, so the same assertion could parse differently on two
        // pods with different locale settings.
        var notBeforeStr = conditionsElement.Attributes?["NotBefore"]?.Value;
        if (notBeforeStr is not null)
        {
            if (!TryParseSamlInstant(notBeforeStr, out var notBefore))
            {
                logger.LogWarning("Assertion NotBefore is present but unparseable: {Value}", notBeforeStr);
                return Fail("Assertion has an unparseable NotBefore condition.");
            }

            if (now + context.ClockSkew < notBefore)
            {
                logger.LogWarning("Assertion not yet valid: NotBefore={NotBefore}, Now={Now}", notBefore, now);
                return Fail("Assertion is not yet valid (NotBefore condition).");
            }
        }

        var notOnOrAfterStr = conditionsElement.Attributes?["NotOnOrAfter"]?.Value;
        if (notOnOrAfterStr is not null)
        {
            if (!TryParseSamlInstant(notOnOrAfterStr, out var notOnOrAfter))
            {
                logger.LogWarning("Assertion NotOnOrAfter is present but unparseable: {Value}", notOnOrAfterStr);
                return Fail("Assertion has an unparseable NotOnOrAfter condition.");
            }

            if (now - context.ClockSkew >= notOnOrAfter)
            {
                logger.LogWarning("Assertion expired: NotOnOrAfter={NotOnOrAfter}, Now={Now}", notOnOrAfter, now);
                return Fail("Assertion has expired (NotOnOrAfter condition).");
            }
        }

        // A condition this SP cannot evaluate makes the assertion Invalid, not unconditioned.
        //
        // Core §2.5.1 is explicit: if any condition is unsupported, the assertion is Invalid. Reading
        // only the children we understand inverted that — an <OneTimeUse> or a <ProxyRestriction> or a
        // condition from a future profile was silently dropped, so an IdP could believe it had
        // constrained an assertion this SP had in fact accepted unconstrained. The two named conditions
        // are listed because this SP genuinely satisfies both: every assertion ID goes through the
        // single-use replay cache before the ACS acts on it, and this SP never re-issues assertions
        // derived from one it consumed, so no proxying restriction can be violated.
        foreach (XmlNode child in conditionsElement.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element)
                continue;

            var known = child.NamespaceURI == SamlConstants.Saml2Assertion
                && child.LocalName is "AudienceRestriction" or "OneTimeUse" or "ProxyRestriction";
            if (!known)
            {
                logger.LogWarning("Assertion carries an unevaluable condition: {Namespace}:{Name}",
                    child.NamespaceURI, child.LocalName);
                return Fail("Assertion carries a condition this SP cannot evaluate.");
            }
        }

        // Every AudienceRestriction must admit us, and any Audience within one satisfies it.
        //
        // Only the first Audience of the first AudienceRestriction was read. SAML 2.0 Core §2.5.1.4
        // makes multiple <AudienceRestriction> elements a CONJUNCTION — the assertion is valid only
        // where all of them hold — so reading one and stopping accepted an assertion whose other
        // restrictions excluded this SP. It also rejected the legitimate case of one restriction
        // listing several audiences with ours second.
        var audienceRestrictions = conditionsElement.SelectNodes("saml:AudienceRestriction", nsManager);
        if (audienceRestrictions is null || audienceRestrictions.Count == 0)
            return Fail("Assertion is missing the required AudienceRestriction/Audience.");

        foreach (XmlNode restriction in audienceRestrictions)
        {
            var audiences = restriction.SelectNodes("saml:Audience", nsManager);
            var admitted = false;
            if (audiences is not null)
            {
                foreach (XmlNode a in audiences)
                {
                    if (string.Equals(a.InnerText?.Trim(), context.ExpectedAudience, StringComparison.OrdinalIgnoreCase))
                    {
                        admitted = true;
                        break;
                    }
                }
            }

            if (!admitted)
            {
                logger.LogWarning("Audience mismatch: expected={Expected} was not admitted by an AudienceRestriction",
                    context.ExpectedAudience);
                return Fail("Assertion audience does not match the expected audience.");
            }
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
        if (!TryParseSamlInstant(dataNotOnOrAfterStr, out var dataNotOnOrAfter))
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
        // still valid and can be presented again. Whichever bound expires first governs: the IdP's own
        // NotOnOrAfter, or the absolute age cap checked above — so an IdP naming a month cannot conscript
        // this SP's replay cache into holding a month of assertion ids either.
        var ageCappedUntil = issueInstant + context.MaxAssertionAge;
        var acceptableUntil = (dataNotOnOrAfter < ageCappedUntil ? dataNotOnOrAfter : ageCappedUntil)
            + context.ClockSkew;

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

        // 12. AuthnStatement — REQUIRED, then SessionIndex/SessionNotOnOrAfter off it.
        //
        // Web Browser SSO §4.1.4.2: the assertions MUST contain at least one <AuthnStatement>
        // reflecting the authentication of the principal. It was read with `?.` throughout, so an
        // assertion carrying none parsed successfully with a null SessionIndex and a null session
        // bound — and both of those are load-bearing. A null SessionIndex leaves the session with
        // nothing for single logout to match on, and a null SessionNotOnOrAfter hands back the
        // unbounded local cookie lifetime. So the one shape of assertion that silently opted out of
        // both session controls was the one that omitted the statement asserting an authentication had
        // happened at all — an attribute-only assertion establishing a login.
        if (assertionElement.SelectSingleNode("saml:AuthnStatement", nsManager) is not XmlElement authnStatementNode)
        {
            logger.LogWarning("SAML assertion carries no AuthnStatement");
            return Fail("Assertion is missing the required AuthnStatement.");
        }

        var sessionIndex = authnStatementNode.Attributes?["SessionIndex"]?.Value;

        // The IdP's stated upper bound on the session it just asserted. It was parsed by nothing, so
        // the local session outlived the authentication it was based on — an IdP that says "this
        // session is good for 8 hours" was silently overruled by whatever the local cookie lifetime
        // happened to be, which is the opposite of what federating to that IdP means.
        DateTimeOffset? sessionNotOnOrAfter = null;
        var sessionNotOnOrAfterStr = authnStatementNode.Attributes?["SessionNotOnOrAfter"]?.Value;
        if (sessionNotOnOrAfterStr is not null)
        {
            if (!TryParseSamlInstant(sessionNotOnOrAfterStr, out var parsedSessionBound))
            {
                logger.LogWarning("AuthnStatement SessionNotOnOrAfter is present but unparseable: {Value}", sessionNotOnOrAfterStr);
                return Fail("Assertion has an unparseable SessionNotOnOrAfter.");
            }

            sessionNotOnOrAfter = parsedSessionBound;
        }

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
            SessionNotOnOrAfter = sessionNotOnOrAfter,
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
        try
        {
            signedXml.LoadXml(signatureElement);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // LoadXml throws for shapes an unauthenticated caller can send: an empty or
            // SignedInfo-less <ds:Signature/> ("Malformed element SignedInfo."), and a Transform whose
            // Algorithm URI .NET does not recognise ("Unknown transform has been encountered.") — the
            // latter reaching LoadXml BEFORE the transform allowlist below can refuse it. The exception
            // propagated out of Parse, past the endpoint's graceful error redirect, and out as a bare
            // 500 with a stack trace under Development. This method's contract is a bool; honour it.
            logger.LogWarning(ex, "SAML signature element could not be loaded — treating as unsigned");
            return false;
        }

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
        var now = DateTimeOffset.UtcNow;
        var expiredCandidates = 0;


        foreach (var cert in trustedCertificates)
        {
            // Expiry is a statement the certificate makes about itself, and it was being discarded.
            //
            // Skipping chain building and revocation is deliberate and correct — trust here comes from
            // pinning the IdP's metadata signing certificates, not from a CA — but NotBefore/NotAfter are a
            // different thing, and nothing consulted them at load time either. Verified: a signature made
            // with a certificate that expired two years ago validated. The only other expiry control on this
            // path is metadata @validUntil, which SAML 2.0 Metadata makes optional, several major IdPs omit,
            // and a pasted connection never re-fetches — so for those connections the certificate set was
            // frozen with no expiry at all.
            //
            // Same skew the assertion time checks allow, so a few seconds of clock disagreement around a
            // rollover boundary is not a hard failure.
            if (!SamlCertificateValidity.IsCurrent(cert, now, logger))
            {
                expiredCandidates++;
                continue;
            }

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

        if (expiredCandidates > 0)
            logger.LogWarning(
                "Signature could not be validated: {Expired} of {Total} trusted certificate(s) are outside "
                + "their validity window. Failing here is what lets the metadata refetch pick up a rollover.",
                expiredCandidates, trustedCertificates.Count);
        else
            logger.LogWarning("Signature could not be validated against any trusted certificate");

        return false;
    }

    /// <summary>
    /// Clock tolerance applied to an IdP signing certificate's validity window.
    /// </summary>
    /// <remarks>
    /// Matches the default assertion skew, so a few seconds of disagreement around a rollover boundary is
    /// not a hard failure. Taken as a constant rather than from the validation context because
    /// <c>ValidateElementSignature</c> is also called for the logout legs, which have no context.
    /// </remarks>

    private static SamlParseResult Fail(string error) => new() { Success = false, Error = error };

    /// <summary>
    /// F54: decrypt an xenc EncryptedAssertion in place. Handles the shapes real IdPs emit: an
    /// EncryptedKey either referenced from the EncryptedData's KeyInfo or as a sibling under the
    /// EncryptedAssertion; RSA-OAEP(SHA1/SHA256) key transport; AES-CBC / 3DES data encryption.
    /// </summary>
    /// <remarks>
    /// <b>RSA-1.5 key transport is refused</b>, and this doc comment used to say it was handled. The switch
    /// below accepts only the two OAEP URLs, deliberately: PKCS#1 v1.5 unwrapping is a
    /// Bleichenbacher/ROBOT oracle, and which padding "succeeded" is attacker-observable through timing
    /// and behaviour. An IdP configured for v1.5 fails every assertion.
    /// <para>
    /// AES-GCM data encryption is also unsupported (an <see cref="EncryptedXml"/> limitation), and the
    /// failure is NOT a clear error: every unsupported algorithm returns the deliberately constant
    /// "Could not decrypt the assertion." — which is the point, since naming the stage that failed is what
    /// makes a padding oracle. Diagnosis comes from the IdP's configuration, not from the message.
    /// </para>
    /// </remarks>
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
