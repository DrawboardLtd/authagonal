using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace Authagonal.Server.Services.Saml;

public sealed record SamlIdpMetadata(
    string SingleSignOnServiceUrl,
    List<X509Certificate2> SigningCertificates,
    string EntityId,
    string? SingleLogoutServiceUrl = null,
    bool WantAuthnRequestsSigned = false);

public sealed class SamlMetadataParser(IHttpClientFactory httpClientFactory)
{
    private static readonly XNamespace Md = "urn:oasis:names:tc:SAML:2.0:metadata";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    public async Task<SamlIdpMetadata> ParseFromUrlAsync(string metadataUrl, CancellationToken ct = default)
    {
        // SafeOutboundHttp re-validates every redirect hop. The guard used to run once, on this URL only,
        // while the client followed redirects automatically — so a 302 from the (admin-configured, but
        // attacker-influenced) metadata host reached a target the guard never saw. It also bounds the
        // response, since an unbounded read on a path reachable from the anonymous ACS is a memory
        // amplifier regardless of where it points.
        // https only. This document IS the trust anchor for the connection — it carries the signing
        // certificates every assertion is checked against — so fetching it over cleartext lets any
        // on-path party substitute their own certificate and mint assertions the SP will accept.
        // Nothing else in the SAML path can compensate for that, because everything downstream trusts
        // whatever this returns.
        if (!Uri.TryCreate(metadataUrl, UriKind.Absolute, out var parsedUrl)
            || parsedUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "SAML IdP metadata must be fetched over https: this document carries the signing " +
                "certificates every assertion is validated against.");
        }

        var client = httpClientFactory.CreateClient("SamlMetadata");
        var response = await SafeOutboundHttp.GetStringAsync(client, metadataUrl, ct: ct);

        // A metadata document that carries its own <ds:Signature> is verified against the key inside
        // it. That is self-referential on its own — it proves internal consistency, not provenance —
        // but it does detect tampering in transit by a party who cannot re-sign, and it refuses a
        // document whose signature is present and BROKEN, which previously parsed happily. Documents
        // with no signature are accepted as before, since many IdPs publish unsigned metadata and
        // https is what carries the trust for those.
        VerifyMetadataSignatureIfPresent(response);

        return Parse(response);
    }

    /// <summary>
    /// Verifies an <c>EntityDescriptor</c>'s enveloped signature when it has one.
    /// </summary>
    /// <remarks>
    /// Throws when a signature is present but does not verify. Silence about a broken signature is
    /// the worst of the three options: it tells an operator who deliberately publishes signed
    /// metadata that the check happened when it did not.
    /// </remarks>
    private static void VerifyMetadataSignatureIfPresent(string metadataXml)
    {
        var doc = SamlResponseParser.LoadHardened(metadataXml);
        var signatures = doc.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#");
        if (signatures.Count == 0) return;

        if (signatures[0] is not System.Xml.XmlElement signatureElement) return;

        var signedXml = new System.Security.Cryptography.Xml.SignedXml(doc);
        try
        {
            signedXml.LoadXml(signatureElement);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new InvalidOperationException("SAML IdP metadata carries a malformed signature.", ex);
        }

        var keyInfoCert = signedXml.KeyInfo
            .OfType<System.Security.Cryptography.Xml.KeyInfoX509Data>()
            .SelectMany(k => k.Certificates?.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>() ?? [])
            .FirstOrDefault();

        if (keyInfoCert is null)
            throw new InvalidOperationException("SAML IdP metadata is signed but carries no verification certificate.");

        using (keyInfoCert)
        {
            if (!signedXml.CheckSignature(keyInfoCert, verifySignatureOnly: true))
                throw new InvalidOperationException("SAML IdP metadata signature did not verify.");
        }
    }

    /// <summary>
    /// F49: condense pasted IdP metadata to a canonical minimal EntityDescriptor holding exactly what
    /// the SP consumes (entityID, SSO endpoints, signing certs). Vendor documents can exceed 100KB
    /// (ADFS FederationMetadata.xml) — past the 64KB Azure Table property cap — while the parts we
    /// use are a few KB of certificates. Parses (validating the paste) and re-emits.
    /// </summary>
    public static string Condense(string metadataXml)
    {
        var parsed = Parse(metadataXml);
        var idpDescriptor = new XElement(Md + "IDPSSODescriptor",
            new XAttribute("protocolSupportEnumeration", SamlConstants.Saml2Protocol));
        if (parsed.WantAuthnRequestsSigned)
            idpDescriptor.Add(new XAttribute("WantAuthnRequestsSigned", "true"));
        idpDescriptor.Add(parsed.SigningCertificates.Select(cert =>
            new XElement(Md + "KeyDescriptor",
                new XAttribute("use", "signing"),
                new XElement(Ds + "KeyInfo",
                    new XElement(Ds + "X509Data",
                        new XElement(Ds + "X509Certificate", Convert.ToBase64String(cert.RawData)))))));
        if (!string.IsNullOrEmpty(parsed.SingleLogoutServiceUrl))
            idpDescriptor.Add(new XElement(Md + "SingleLogoutService",
                new XAttribute("Binding", SamlConstants.HttpRedirectBinding),
                new XAttribute("Location", parsed.SingleLogoutServiceUrl)));
        idpDescriptor.Add(new XElement(Md + "SingleSignOnService",
            new XAttribute("Binding", SamlConstants.HttpRedirectBinding),
            new XAttribute("Location", parsed.SingleSignOnServiceUrl)));
        var root = new XElement(Md + "EntityDescriptor",
            new XAttribute("entityID", parsed.EntityId),
            idpDescriptor);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    public static SamlIdpMetadata Parse(string metadataXml)
    {
        var doc = XDocument.Parse(metadataXml);
        var root = doc.Root
            ?? throw new InvalidOperationException("Metadata XML has no root element.");

        // Extract EntityID from root <EntityDescriptor>
        var entityId = root.Attribute("entityID")?.Value
            ?? throw new InvalidOperationException("Metadata missing entityID attribute.");

        // Find <IDPSSODescriptor>
        var idpDescriptor = root.Element(Md + "IDPSSODescriptor")
            ?? throw new InvalidOperationException("Metadata missing IDPSSODescriptor element.");

        // Extract SingleSignOnService with HTTP-Redirect binding
        string? ssoUrl = null;
        foreach (var ssoElement in idpDescriptor.Elements(Md + "SingleSignOnService"))
        {
            var binding = ssoElement.Attribute("Binding")?.Value;
            if (string.Equals(binding, SamlConstants.HttpRedirectBinding, StringComparison.Ordinal))
            {
                ssoUrl = ssoElement.Attribute("Location")?.Value;
                break;
            }
        }

        // Fallback: try HTTP-POST binding if no redirect binding found
        if (string.IsNullOrEmpty(ssoUrl))
        {
            foreach (var ssoElement in idpDescriptor.Elements(Md + "SingleSignOnService"))
            {
                var binding = ssoElement.Attribute("Binding")?.Value;
                if (string.Equals(binding, SamlConstants.HttpPostBinding, StringComparison.Ordinal))
                {
                    ssoUrl = ssoElement.Attribute("Location")?.Value;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(ssoUrl))
            throw new InvalidOperationException("Metadata missing SingleSignOnService with HTTP-Redirect or HTTP-POST binding.");

        // Extract signing certificates from <KeyDescriptor>
        var certificates = new List<X509Certificate2>();
        foreach (var keyDescriptor in idpDescriptor.Elements(Md + "KeyDescriptor"))
        {
            var use = keyDescriptor.Attribute("use")?.Value;

            // Include if use="signing" or if use attribute is omitted
            // (Azure AD sometimes omits the use attribute — treat as signing)
            if (use is not null && !string.Equals(use, "signing", StringComparison.OrdinalIgnoreCase))
                continue;

            var certElement = keyDescriptor
                .Element(Ds + "KeyInfo")?
                .Element(Ds + "X509Data")?
                .Element(Ds + "X509Certificate");

            if (certElement is null)
                continue;

            var certBase64 = certElement.Value.Trim();
            var certBytes = Convert.FromBase64String(certBase64);
            certificates.Add(X509CertificateLoader.LoadCertificate(certBytes));
        }

        if (certificates.Count == 0)
            throw new InvalidOperationException("Metadata contains no signing certificates.");

        // Single logout endpoint (F55) — Redirect binding preferred, POST fallback
        string? sloUrl = null;
        foreach (var binding in new[] { SamlConstants.HttpRedirectBinding, SamlConstants.HttpPostBinding })
        {
            foreach (var sloElement in idpDescriptor.Elements(Md + "SingleLogoutService"))
            {
                if (string.Equals(sloElement.Attribute("Binding")?.Value, binding, StringComparison.Ordinal))
                {
                    sloUrl = sloElement.Attribute("Location")?.Value;
                    break;
                }
            }
            if (!string.IsNullOrEmpty(sloUrl)) break;
        }

        var wantSignedRequests = string.Equals(
            idpDescriptor.Attribute("WantAuthnRequestsSigned")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        return new SamlIdpMetadata(ssoUrl, certificates, entityId, sloUrl, wantSignedRequests);
    }
}
