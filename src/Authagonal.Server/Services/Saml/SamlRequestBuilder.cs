using System.IO.Compression;
using System.Text;

namespace Authagonal.Server.Services.Saml;

public static class SamlRequestBuilder
{
    public static string BuildAuthnRequestUrl(string requestId, string issuer, string acsUrl, string destination)
    {
        var issueInstant = DateTime.UtcNow.ToString("o");

        var xml = $"""
            <samlp:AuthnRequest
                xmlns:samlp="{SamlConstants.Saml2Protocol}"
                ID="{requestId}"
                Version="2.0"
                IssueInstant="{issueInstant}"
                Destination="{destination}"
                AssertionConsumerServiceURL="{acsUrl}"
                ProtocolBinding="{SamlConstants.HttpPostBinding}">
              <saml:Issuer xmlns:saml="{SamlConstants.Saml2Assertion}">{issuer}</saml:Issuer>
              <samlp:NameIDPolicy Format="{SamlConstants.NameIdEmail}" AllowCreate="true" />
            </samlp:AuthnRequest>
            """;

        // 1. UTF-8 encode
        var bytes = Encoding.UTF8.GetBytes(xml);

        // 2. Deflate compress (raw deflate per SAML HTTP-Redirect spec — NOT GZip)
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bytes, 0, bytes.Length);
        }

        // 3. Base64 encode
        var base64 = Convert.ToBase64String(output.ToArray());

        // 4. URL encode
        var urlEncoded = Uri.EscapeDataString(base64);

        // Return the full redirect URL (caller appends &RelayState=...)
        return $"{destination}?SAMLRequest={urlEncoded}";
    }
}
