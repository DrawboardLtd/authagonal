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
    public string? AssertionId { get; init; }
}

public sealed class SamlResponseParser(ILogger<SamlResponseParser> logger)
{
    /// <summary>
    /// The exact failure string produced when neither the Response nor the Assertion carried a
    /// validatable signature. The ACS matches on this to trigger a one-shot metadata refetch (F52:
    /// IdP cert rollover mid-cache-window would otherwise fail logins until the TTL lapses).
    /// </summary>
    public const string SignatureFailure = "No valid signature found on Response or Assertion.";

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
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
            };
            using var stringReader = new System.IO.StringReader(System.Text.Encoding.UTF8.GetString(responseBytes));
            using var xmlReader = XmlReader.Create(stringReader, readerSettings);
            doc.Load(xmlReader);
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
                    logger.LogWarning(ex, "Failed to decrypt EncryptedAssertion");
                    return Fail($"Failed to decrypt the EncryptedAssertion: {ex.Message}");
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

        // 9. Validate SubjectConfirmation
        var subjectConfirmationNode = assertionElement.SelectSingleNode(
            "saml:Subject/saml:SubjectConfirmation", nsManager);
        if (subjectConfirmationNode is XmlElement subjectConfirmation)
        {
            var method = subjectConfirmation.Attributes?["Method"]?.Value;
            if (!string.Equals(method, SamlConstants.BearerConfirmation, StringComparison.Ordinal))
            {
                logger.LogWarning("Unsupported SubjectConfirmation method: {Method}", method);
                return Fail($"Unsupported SubjectConfirmation method: {method}");
            }

            var confirmationData = subjectConfirmation.SelectSingleNode(
                "saml:SubjectConfirmationData", nsManager) as XmlElement;
            if (confirmationData is not null)
            {
                var recipient = confirmationData.Attributes?["Recipient"]?.Value;
                if (!string.IsNullOrEmpty(recipient) &&
                    !string.Equals(recipient, context.ExpectedAcsUrl, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Recipient mismatch: expected={Expected}, actual={Actual}",
                        context.ExpectedAcsUrl, recipient);
                    return Fail("SubjectConfirmationData Recipient does not match the expected ACS URL.");
                }

                var dataNotOnOrAfterStr = confirmationData.Attributes?["NotOnOrAfter"]?.Value;
                if (dataNotOnOrAfterStr is not null &&
                    DateTimeOffset.TryParse(dataNotOnOrAfterStr, out var dataNotOnOrAfter))
                {
                    if (now - context.ClockSkew >= dataNotOnOrAfter)
                    {
                        logger.LogWarning("SubjectConfirmationData expired: NotOnOrAfter={NotOnOrAfter}, Now={Now}",
                            dataNotOnOrAfter, now);
                        return Fail("SubjectConfirmationData has expired.");
                    }
                }

                var dataInResponseTo = confirmationData.Attributes?["InResponseTo"]?.Value;
                if (context.ExpectedInResponseTo is not null && dataInResponseTo is not null &&
                    !string.Equals(dataInResponseTo, context.ExpectedInResponseTo, StringComparison.Ordinal))
                {
                    logger.LogWarning("SubjectConfirmationData InResponseTo mismatch");
                    return Fail("SubjectConfirmationData InResponseTo does not match.");
                }
            }
        }

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
            AssertionId = assertionId
        };
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

        var signedXml = new SignedXml(element);
        signedXml.LoadXml(signatureElement);

        // SECURITY: Verify the Reference URI matches the signed element's ID
        // This prevents signature wrapping attacks
        if (signedXml.SignedInfo?.References is { Count: > 0 })
        {
            var reference = (Reference)signedXml.SignedInfo.References[0]!;
            var referenceUri = reference.Uri;

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

        // Key transport: try the paddings real IdPs use, preferring what the algorithm URI declares.
        var keyAlgorithm = encryptedKey.EncryptionMethod?.KeyAlgorithm ?? "";
        var paddings = keyAlgorithm switch
        {
            EncryptedXml.XmlEncRSAOAEPUrl => new[] { System.Security.Cryptography.RSAEncryptionPadding.OaepSHA1, System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256 },
            EncryptedXml.XmlEncRSA15Url => new[] { System.Security.Cryptography.RSAEncryptionPadding.Pkcs1 },
            // xenc11 rsa-oaep (digest negotiated separately) or anything else: try the common three.
            _ => new[] { System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256, System.Security.Cryptography.RSAEncryptionPadding.OaepSHA1, System.Security.Cryptography.RSAEncryptionPadding.Pkcs1 },
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
            throw new InvalidOperationException("Could not unwrap the assertion encryption key with the SP private key (wrong SP cert, or an unsupported key-transport algorithm).");

        var dataAlgorithm = encryptedData.EncryptionMethod?.KeyAlgorithm ?? "";
        System.Security.Cryptography.SymmetricAlgorithm symmetric = dataAlgorithm switch
        {
            EncryptedXml.XmlEncAES128Url or EncryptedXml.XmlEncAES192Url or EncryptedXml.XmlEncAES256Url
                => System.Security.Cryptography.Aes.Create(),
            EncryptedXml.XmlEncTripleDESUrl => System.Security.Cryptography.TripleDES.Create(),
            _ => throw new InvalidOperationException($"Unsupported assertion encryption algorithm '{dataAlgorithm}' (AES-GCM is not supported; configure AES-CBC at the IdP)."),
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
