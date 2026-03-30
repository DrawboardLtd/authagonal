using System.IO.Compression;
using System.Text;

namespace Authagonal.Server.Services.Saml;

public static class SamlRequestBuilder
{
    public static string BuildAuthnRequestUrl(string requestId, string issuer, string acsUrl, string destination, string? loginHint = null)
    {
        var issueInstant = DateTime.UtcNow.ToString("o");

        var subjectXml = "";
        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            var escapedHint = System.Security.SecurityElement.Escape(loginHint);
            subjectXml = $"""
                          <saml:Subject xmlns:saml="{SamlConstants.Saml2Assertion}">
                            <saml:NameID Format="{SamlConstants.NameIdEmail}">{escapedHint}</saml:NameID>
                          </saml:Subject>
                """;
        }

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
              {subjectXml}<samlp:NameIDPolicy Format="{SamlConstants.NameIdEmail}" AllowCreate="true" />
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

        // Build the full redirect URL (caller appends &RelayState=...)
        var url = $"{destination}?SAMLRequest={urlEncoded}";

        // Append login_hint for IdPs that support it (e.g. Entra ID, Google)
        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            url += $"&login_hint={Uri.EscapeDataString(loginHint)}";
        }

        return url;
    }
}
