using System.IO.Compression;
using System.Text;

namespace Authagonal.Server.Services.Saml;

/// <summary>
/// Builds the SP's outbound messages — AuthnRequest, LogoutRequest, LogoutResponse — in the SAML 2.0
/// HTTP-Redirect binding (Bindings §3.4.4.1): DEFLATE, base64, URL-encode into a query string.
/// <para>
/// That is the only binding implemented, and it is why <see cref="SamlMetadataParser"/> accepts only
/// endpoints published for it: every destination reaching these methods carries
/// <see cref="SamlBinding.HttpRedirect"/>. HTTP-POST (§3.5.4) would need base64 without DEFLATE in a
/// self-submitting form with a noscript control, and its signature inside the message as XML-DSig
/// rather than over the query string the way <see cref="SamlRedirectBinding"/> signs — a second
/// implementation, not a branch.
/// </para>
/// </summary>
public static class SamlRequestBuilder
{
    /// <summary>
    /// Sentinel for <paramref name="nameIdFormat"/>: omit the NameIDPolicy element entirely. The
    /// ADFS-safe setting — ADFS fails the whole login (MSIS7070) when its relying-party claim rules
    /// don't emit the requested format. F51.
    /// </summary>
    public const string NameIdFormatNone = "none";

    /// <summary>
    /// XML-escapes a value on its way into one of these interpolated documents.
    /// </summary>
    /// <remarks>
    /// Every interpolated value goes through this. The values are operator-configured (entityID, ACS
    /// URL, the IdP's SSO/SLO endpoint) rather than caller-supplied, so this is not a live injection —
    /// but a <c>&amp;</c> or <c>"</c> is legal in a URI and ordinary in a query string, and an
    /// unescaped one either produced a malformed document the IdP rejected with an error naming
    /// neither the value nor the reason, or — for a <c>"</c> — closed the attribute and let the rest
    /// of the configured string become markup in a message this SP then signs with its own key.
    /// </remarks>
    private static string X(string? value) => System.Security.SecurityElement.Escape(value ?? string.Empty);

    public static string BuildAuthnRequestUrl(string requestId, string issuer, string acsUrl, string destination, string? loginHint = null, string? nameIdFormat = null)
    {
        var issueInstant = DateTime.UtcNow.ToString("o");

        // Do NOT embed a <saml:Subject> in the AuthnRequest. It's optional per the
        // SAML spec, and Entra rejects any AuthnRequest that carries one
        // (AADSTS900236: "The SAML authentication request property 'Subject' is not
        // supported and must not be set."). The login hint is conveyed via the
        // login_hint query parameter below instead, which Entra (and Google) honour.
        // NameIDPolicy: null keeps the historic emailAddress default (existing Entra connections
        // rely on the NameID-email fallback); "none" omits the element (ADFS-safe); anything else
        // is sent verbatim.
        var nameIdPolicy = string.Equals(nameIdFormat, NameIdFormatNone, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"""
              <samlp:NameIDPolicy Format="{X(nameIdFormat ?? SamlConstants.NameIdEmail)}" AllowCreate="true" />
            """;

        var xml = $"""
            <samlp:AuthnRequest
                xmlns:samlp="{SamlConstants.Saml2Protocol}"
                ID="{X(requestId)}"
                Version="2.0"
                IssueInstant="{issueInstant}"
                Destination="{X(destination)}"
                AssertionConsumerServiceURL="{X(acsUrl)}"
                ProtocolBinding="{SamlConstants.HttpPostBinding}">
              <saml:Issuer xmlns:saml="{SamlConstants.Saml2Assertion}">{X(issuer)}</saml:Issuer>{nameIdPolicy}
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

    /// <summary>F55: SP-initiated LogoutRequest, redirect binding.</summary>
    public static string BuildLogoutRequestUrl(
        string requestId, string issuer, string destination,
        string nameId, string? nameIdFormat, string? sessionIndex)
    {
        var issueInstant = DateTime.UtcNow.ToString("o");
        var formatAttr = string.IsNullOrEmpty(nameIdFormat)
            ? ""
            : $@" Format=""{X(nameIdFormat)}""";
        var sessionIndexElement = string.IsNullOrEmpty(sessionIndex)
            ? ""
            : $"<samlp:SessionIndex>{X(sessionIndex)}</samlp:SessionIndex>";

        // Destination is the IdP metadata's SingleLogoutService Location and was the one value here
        // left raw while its neighbours were escaped — the same defect the AuthnRequest carried, in
        // the same file. A `"` or `&` in a published SLO Location breaks out of the attribute.
        var xml = $"""
            <samlp:LogoutRequest
                xmlns:samlp="{SamlConstants.Saml2Protocol}"
                ID="{X(requestId)}"
                Version="2.0"
                IssueInstant="{issueInstant}"
                Destination="{X(destination)}">
              <saml:Issuer xmlns:saml="{SamlConstants.Saml2Assertion}">{X(issuer)}</saml:Issuer>
              <saml:NameID xmlns:saml="{SamlConstants.Saml2Assertion}"{formatAttr}>{X(nameId)}</saml:NameID>{sessionIndexElement}
            </samlp:LogoutRequest>
            """;

        return $"{destination}{(destination.Contains('?') ? '&' : '?')}SAMLRequest={DeflateAndEncode(xml)}";
    }

    /// <summary>F55: LogoutResponse (answering an IdP-initiated LogoutRequest), redirect binding.</summary>
    public static string BuildLogoutResponseUrl(string inResponseTo, string issuer, string destination)
    {
        var issueInstant = DateTime.UtcNow.ToString("o");
        var responseId = "_" + Guid.NewGuid().ToString("N");

        var xml = $"""
            <samlp:LogoutResponse
                xmlns:samlp="{SamlConstants.Saml2Protocol}"
                ID="{responseId}"
                Version="2.0"
                IssueInstant="{issueInstant}"
                Destination="{X(destination)}"
                InResponseTo="{X(inResponseTo)}">
              <saml:Issuer xmlns:saml="{SamlConstants.Saml2Assertion}">{X(issuer)}</saml:Issuer>
              <samlp:Status><samlp:StatusCode Value="{SamlConstants.StatusSuccess}"/></samlp:Status>
            </samlp:LogoutResponse>
            """;

        return $"{destination}{(destination.Contains('?') ? '&' : '?')}SAMLResponse={DeflateAndEncode(xml)}";
    }

    private static string DeflateAndEncode(string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bytes, 0, bytes.Length);
        }
        return Uri.EscapeDataString(Convert.ToBase64String(output.ToArray()));
    }
}
