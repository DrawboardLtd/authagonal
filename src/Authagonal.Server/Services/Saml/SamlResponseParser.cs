using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Authagonal.Server.Services.Saml;

public sealed record SamlResponseValidationContext(
    string ExpectedAcsUrl,
    string ExpectedAudience,
    string? ExpectedInResponseTo,
    IReadOnlyList<X509Certificate2> TrustedCertificates,
    TimeSpan ClockSkew = default)
{
    public TimeSpan ClockSkew { get; init; } = ClockSkew == default ? TimeSpan.FromMinutes(5) : ClockSkew;
}

public sealed record SamlParseResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? NameId { get; init; }
    public string? NameIdFormat { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new();
    public string? SessionIndex { get; init; }
}

public sealed class SamlResponseParser(ILogger<SamlResponseParser> logger)
{
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

        // 2. Load into XmlDocument (PreserveWhitespace = true is critical for signature validation)
        var doc = new XmlDocument { PreserveWhitespace = true };
        try
        {
            doc.LoadXml(System.Text.Encoding.UTF8.GetString(responseBytes));
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
        var assertionNode = responseElement.SelectSingleNode("saml:Assertion", nsManager);
        if (assertionNode is not XmlElement assertionElement)
            return Fail("SAML response does not contain an Assertion.");

        var responseSignatureValid = ValidateElementSignature(
            responseElement, context.TrustedCertificates, logger);
        var assertionSignatureValid = ValidateElementSignature(
            assertionElement, context.TrustedCertificates, logger);

        if (!responseSignatureValid && !assertionSignatureValid)
        {
            logger.LogWarning("No valid signature found on Response or Assertion");
            return Fail("No valid signature found on Response or Assertion.");
        }

        // 8. Validate Assertion Conditions
        var conditionsNode = assertionElement.SelectSingleNode("saml:Conditions", nsManager);
        if (conditionsNode is XmlElement conditionsElement)
        {
            var now = DateTimeOffset.UtcNow;

            var notBeforeStr = conditionsElement.Attributes?["NotBefore"]?.Value;
            if (notBeforeStr is not null && DateTimeOffset.TryParse(notBeforeStr, out var notBefore))
            {
                if (now + context.ClockSkew < notBefore)
                {
                    logger.LogWarning("Assertion not yet valid: NotBefore={NotBefore}, Now={Now}",
                        notBefore, now);
                    return Fail("Assertion is not yet valid (NotBefore condition).");
                }
            }

            var notOnOrAfterStr = conditionsElement.Attributes?["NotOnOrAfter"]?.Value;
            if (notOnOrAfterStr is not null && DateTimeOffset.TryParse(notOnOrAfterStr, out var notOnOrAfter))
            {
                if (now - context.ClockSkew >= notOnOrAfter)
                {
                    logger.LogWarning("Assertion expired: NotOnOrAfter={NotOnOrAfter}, Now={Now}",
                        notOnOrAfter, now);
                    return Fail("Assertion has expired (NotOnOrAfter condition).");
                }
            }

            // Validate AudienceRestriction
            var audienceNode = conditionsElement.SelectSingleNode(
                "saml:AudienceRestriction/saml:Audience", nsManager);
            var audience = audienceNode?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(audience) &&
                !string.Equals(audience, context.ExpectedAudience, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Audience mismatch: expected={Expected}, actual={Actual}",
                    context.ExpectedAudience, audience);
                return Fail("Assertion audience does not match the expected audience.");
            }
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
                    var now = DateTimeOffset.UtcNow;
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

        // 11. Extract Attributes
        var attributes = new Dictionary<string, string>();
        var attributeNodes = assertionElement.SelectNodes(
            "saml:AttributeStatement/saml:Attribute", nsManager);
        if (attributeNodes is not null)
        {
            foreach (XmlElement attrElement in attributeNodes)
            {
                var attrName = attrElement.Attributes?["Name"]?.Value;
                if (string.IsNullOrEmpty(attrName))
                    continue;

                var valueNode = attrElement.SelectSingleNode("saml:AttributeValue", nsManager);
                var attrValue = valueNode?.InnerText?.Trim();
                if (attrValue is not null)
                    attributes[attrName] = attrValue;
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
            SessionIndex = sessionIndex
        };
    }

    private static bool ValidateElementSignature(
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

        // Try each trusted certificate
        foreach (var cert in trustedCertificates)
        {
            try
            {
                if (signedXml.CheckSignature(cert, verifySignatureOnly: true))
                {
                    logger.LogDebug("Signature validated against certificate: {Thumbprint}",
                        cert.Thumbprint);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Some certs may not be applicable (different algorithm, etc.)
                logger.LogDebug(ex, "Signature check failed with certificate {Thumbprint}",
                    cert.Thumbprint);
            }
        }

        logger.LogWarning("Signature could not be validated against any trusted certificate");
        return false;
    }

    private static SamlParseResult Fail(string error) => new() { Success = false, Error = error };
}
