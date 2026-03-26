using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace Authagonal.Server.Services.Saml;

public sealed record SamlIdpMetadata(
    string SingleSignOnServiceUrl,
    List<X509Certificate2> SigningCertificates,
    string EntityId);

public sealed class SamlMetadataParser(IHttpClientFactory httpClientFactory)
{
    private static readonly XNamespace Md = "urn:oasis:names:tc:SAML:2.0:metadata";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    public async Task<SamlIdpMetadata> ParseFromUrlAsync(string metadataUrl, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("SamlMetadata");
        var response = await client.GetStringAsync(metadataUrl, ct);
        return Parse(response);
    }

    internal static SamlIdpMetadata Parse(string metadataXml)
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

        return new SamlIdpMetadata(ssoUrl, certificates, entityId);
    }
}
