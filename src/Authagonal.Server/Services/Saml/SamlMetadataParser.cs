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
        if (!OutboundUrlValidator.IsSafe(metadataUrl))
            throw new InvalidOperationException("SAML metadata URL is not an allowed external endpoint.");

        var client = httpClientFactory.CreateClient("SamlMetadata");
        var response = await client.GetStringAsync(metadataUrl, ct);
        return Parse(response);
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
