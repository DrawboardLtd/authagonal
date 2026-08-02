using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace Authagonal.Server.Services.Saml;

/// <summary>
/// A SAML 2.0 binding, as far as outbound messages are concerned. <see cref="SamlRequestBuilder"/>
/// implements <see cref="HttpRedirect"/> only, and this enum exists so that fact is stated in the
/// data rather than assumed by the caller — see <see cref="SamlIdpMetadata"/>.
/// </summary>
public enum SamlBinding
{
    /// <summary>urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect — DEFLATE, base64, query string.</summary>
    HttpRedirect,

    /// <summary>urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST — base64 without DEFLATE, form POST.</summary>
    HttpPost,
}

/// <param name="SingleSignOnServiceBinding">
/// The binding the <paramref name="SingleSignOnServiceUrl"/> was selected for. Always
/// <see cref="SamlBinding.HttpRedirect"/> today — a URL and the encoding it expects are one fact, and
/// keeping them apart is what let a POST-only endpoint be handed to a redirect-binding builder (F293).
/// </param>
/// <param name="SingleLogoutServiceBinding">As above, for <paramref name="SingleLogoutServiceUrl"/>.</param>
/// <param name="ValidUntil">
/// <c>@validUntil</c> — the instant after which the IdP says this document must not be trusted.
/// </param>
/// <param name="CacheDuration">
/// <c>@cacheDuration</c> — the longest the IdP permits this document to be cached.
/// </param>
public sealed record SamlIdpMetadata(
    string SingleSignOnServiceUrl,
    List<X509Certificate2> SigningCertificates,
    string EntityId,
    string? SingleLogoutServiceUrl = null,
    bool WantAuthnRequestsSigned = false,
    SamlBinding SingleSignOnServiceBinding = SamlBinding.HttpRedirect,
    SamlBinding SingleLogoutServiceBinding = SamlBinding.HttpRedirect,
    DateTimeOffset? ValidUntil = null,
    TimeSpan? CacheDuration = null);

/// <param name="allowlist">
/// The operator's internal-destination allowlist (<c>Auth:AllowedInternalTargets</c>), applied to the
/// metadata URL and every redirect it takes. Optional, and its absence is the strict posture: a host that
/// composes its own container and never registers one simply refuses every internal target, which is what
/// the guard did before the allowlist existed. It has to be the SAME list the "SamlMetadata" client's
/// connect callback was given — the URL check and the socket check both have to permit an on-premises IdP
/// or the fetch fails in whichever layer was left out.
/// </param>
public sealed class SamlMetadataParser(
    IHttpClientFactory httpClientFactory,
    Authagonal.Core.Services.OutboundAllowlist? allowlist = null)
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
        var response = await SafeOutboundHttp.GetStringAsync(client, metadataUrl, ct: ct, allowlist: allowlist);

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
    /// <para>
    /// The re-emitted endpoints are labelled HTTP-Redirect. That is only true because
    /// <see cref="Parse"/> now selects nothing else; while the POST fallback existed, condensing a
    /// POST-only document silently relabelled a POST endpoint as a redirect one and persisted the lie.
    /// </para>
    /// </summary>
    public static string Condense(string metadataXml)
    {
        // Pasted metadata gets the same signature treatment as fetched metadata. It arrives over the
        // authenticated admin API rather than off the wire, so provenance is the operator's — but a
        // document whose signature is present and BROKEN is not a document the operator meant to paste,
        // and accepting it silently tells an operator who publishes signed metadata that the check
        // happened when it did not. This is the only point a pasted document is ever verified: Condense
        // strips the signature (it re-emits a minimal descriptor), and the stored form is what Parse
        // sees at every login afterwards.
        VerifyMetadataSignatureIfPresent(metadataXml);

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
        // Carried through, not dropped. Condensing used to discard the IdP's own expiry, so a pasted
        // document — which is never re-fetched — became permanently valid at the moment it was stored,
        // and the one statement the IdP makes about when to stop trusting these certificates was erased
        // by the act of saving them.
        if (parsed.ValidUntil is { } validUntil)
            root.SetAttributeValue("validUntil", validUntil.UtcDateTime.ToString("O"));
        if (parsed.CacheDuration is { } cacheDuration)
            root.SetAttributeValue("cacheDuration", System.Xml.XmlConvert.ToString(cacheDuration));
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

        // validUntil is the IdP's own expiry on this document (SAML 2.0 Metadata §2.2.1: consumers MUST
        // NOT use it past that instant). Ignoring it removed the only revocation channel metadata has:
        // an IdP that pulls a compromised signing certificate republishes with a past validUntil, and
        // until this was read that republication had no effect here — the certificate kept validating
        // assertions until someone noticed. Fail closed rather than warn, because "expired trust anchor"
        // and "trust anchor an attacker wants you to keep using" look identical from here.
        DateTimeOffset? validUntil = null;
        if (root.Attribute("validUntil")?.Value is { } validUntilRaw)
        {
            if (!SamlResponseParser.TryParseSamlInstant(validUntilRaw, out var parsedValidUntil))
                throw new InvalidOperationException($"Metadata validUntil is not a valid timestamp: {validUntilRaw}");
            if (parsedValidUntil <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException(
                    $"IdP metadata expired at {parsedValidUntil:O} (its own validUntil). Re-fetch it from the " +
                    "IdP — the signing certificates it carries are no longer published as current.");
            validUntil = parsedValidUntil;
        }

        // cacheDuration bounds how long a consumer may hold the document. Read here so the memory cache
        // can honour it: caching for a fixed hour past a document that says PT5M is the same staleness
        // problem in slower motion.
        TimeSpan? cacheDuration = null;
        if (root.Attribute("cacheDuration")?.Value is { } cacheDurationRaw)
        {
            try { cacheDuration = System.Xml.XmlConvert.ToTimeSpan(cacheDurationRaw); }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"Metadata cacheDuration is not a valid xs:duration: {cacheDurationRaw}");
            }
        }

        // Find <IDPSSODescriptor>
        var idpDescriptor = root.Element(Md + "IDPSSODescriptor")
            ?? throw new InvalidOperationException("Metadata missing IDPSSODescriptor element.");

        // Extract SingleSignOnService with HTTP-Redirect binding.
        //
        // F293: this used to fall back to an HTTP-POST endpoint when no HTTP-Redirect one was
        // published, which made a POST-only IdP (some ADFS and Ping deployments, and anything hardened
        // to refuse query-string requests) look configured while being unsupported. The URL is only
        // ever consumed by SamlRequestBuilder, which DEFLATEs, base64s and URL-encodes into a query
        // string — the HTTP-Redirect encoding of Bindings §3.4.4.1. HTTP-POST (§3.5.4) is base64
        // without DEFLATE, delivered as a form POST, and its signature lives in the message as XML-DSig
        // rather than in the query string the way SamlRedirectBinding.Sign puts it. So the fallback
        // could not have worked: every login GET a deflated AuthnRequest at an endpoint expecting a
        // form, and the IdP answered with an opaque error.
        //
        // Supporting POST outbound is a feature (an XML signing path for requests, form rendering with
        // a noscript control, at three call sites), not a fix. Until it exists the honest behaviour is
        // to refuse the connection here, where the message names the actual problem — for pasted
        // metadata that surfaces as a 400 at connection-create time, and for a metadata URL at the
        // first login rather than as an IdP-side error with no explanation.
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

        if (string.IsNullOrEmpty(ssoUrl))
        {
            var postOnly = idpDescriptor.Elements(Md + "SingleSignOnService").Any(e =>
                string.Equals(e.Attribute("Binding")?.Value, SamlConstants.HttpPostBinding, StringComparison.Ordinal));
            throw new InvalidOperationException(postOnly
                ? "IdP publishes no HTTP-Redirect SingleSignOnService endpoint (only HTTP-POST). " +
                  "Authagonal sends AuthnRequests using the HTTP-Redirect binding, so the IdP must " +
                  "publish a SingleSignOnService with Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect\"."
                : "Metadata missing SingleSignOnService with HTTP-Redirect binding.");
        }

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

        // Single logout endpoint (F55) — HTTP-Redirect only, for the same reason as SSO above. The POST
        // fallback is dropped rather than made fatal: SLO is best-effort by contract (SloAsync already
        // ends the local session and redirects when there is no IdP endpoint), so a POST-only IdP loses
        // upstream logout instead of losing login. Sending it a deflated query-string LogoutRequest
        // would not have logged the user out either — it would just have failed less visibly.
        string? sloUrl = null;
        foreach (var sloElement in idpDescriptor.Elements(Md + "SingleLogoutService"))
        {
            if (string.Equals(sloElement.Attribute("Binding")?.Value, SamlConstants.HttpRedirectBinding, StringComparison.Ordinal))
            {
                sloUrl = sloElement.Attribute("Location")?.Value;
                break;
            }
        }

        var wantSignedRequests = string.Equals(
            idpDescriptor.Attribute("WantAuthnRequestsSigned")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        return new SamlIdpMetadata(
            ssoUrl, certificates, entityId, sloUrl, wantSignedRequests,
            SamlBinding.HttpRedirect, SamlBinding.HttpRedirect, validUntil, cacheDuration);
    }
}
