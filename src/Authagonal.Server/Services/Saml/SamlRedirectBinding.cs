using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Authagonal.Server.Services.Saml;

/// <summary>
/// SAML HTTP-Redirect binding signature helpers (F54/F55). Per the binding spec, the signature is
/// computed over the RAW query-encoded concatenation of SAMLRequest|SAMLResponse, then RelayState
/// (if present), then SigAlg — in that order, exactly as they appear on the wire.
/// </summary>
public static class SamlRedirectBinding
{
    public const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    public const string RsaSha1 = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";

    /// <summary>
    /// Append SigAlg + Signature (RSA-SHA256) to a redirect-binding URL. Only the spec-defined
    /// parameters participate in the signature; any other query params (login_hint) are unsigned,
    /// which the binding permits.
    /// </summary>
    public static string Sign(string url, RSA key)
    {
        var uri = new Uri(url);
        var query = uri.Query.TrimStart('?');
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

        string? Find(string name) => parts.FirstOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal));

        var message = Find("SAMLRequest") ?? Find("SAMLResponse")
            ?? throw new InvalidOperationException("URL carries no SAMLRequest/SAMLResponse to sign.");
        var relayState = Find("RelayState");
        var sigAlg = "SigAlg=" + Uri.EscapeDataString(RsaSha256);

        var toSign = message + (relayState is null ? "" : "&" + relayState) + "&" + sigAlg;
        var signature = key.SignData(Encoding.UTF8.GetBytes(toSign), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return url + "&" + sigAlg + "&Signature=" + Uri.EscapeDataString(Convert.ToBase64String(signature));
    }

    /// <summary>
    /// Verify a redirect-binding signature from the RAW query string as received (do not re-encode:
    /// the signature covers the sender's exact percent-encoding). Returns false when no Signature
    /// parameter is present or nothing validates against the trusted certs.
    /// </summary>
    public static bool Verify(string rawQuery, IReadOnlyList<X509Certificate2> trustedCertificates)
    {
        var query = rawQuery.TrimStart('?');
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

        string? Find(string name) => parts.FirstOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal));

        var message = Find("SAMLRequest") ?? Find("SAMLResponse");
        var sigAlgPart = Find("SigAlg");
        var signaturePart = Find("Signature");
        if (message is null || sigAlgPart is null || signaturePart is null)
            return false;

        var sigAlg = Uri.UnescapeDataString(sigAlgPart["SigAlg=".Length..]);
        var hash = sigAlg switch
        {
            RsaSha256 => HashAlgorithmName.SHA256,
            RsaSha1 => HashAlgorithmName.SHA1,
            _ => default,
        };
        if (hash == default)
            return false;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Uri.UnescapeDataString(signaturePart["Signature=".Length..]));
        }
        catch (FormatException)
        {
            return false;
        }

        var relayState = Find("RelayState");
        var toVerify = Encoding.UTF8.GetBytes(
            message + (relayState is null ? "" : "&" + relayState) + "&" + sigAlgPart);

        foreach (var cert in trustedCertificates)
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null && rsa.VerifyData(toVerify, signature, hash, RSASignaturePadding.Pkcs1))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Largest message the redirect binding will accept, compressed or inflated.
    /// </summary>
    /// <remarks>
    /// Generous for any real AuthnRequest or LogoutRequest — those are a few kilobytes — and the
    /// point is only to have a ceiling at all. DEFLATE reaches ratios above 1000:1 on repetitive
    /// input, so an unbounded inflate on an anonymous endpoint that runs BEFORE any signature check
    /// turns a few hundred kilobytes of query string into gigabytes of allocation.
    /// </remarks>
    private const int MaxRedirectMessageBytes = 256 * 1024;

    /// <summary>Inflate a redirect-binding SAMLRequest/SAMLResponse query value (base64 raw-deflate).</summary>
    /// <exception cref="InvalidOperationException">The compressed or inflated size exceeds the cap.</exception>
    public static string Inflate(string base64Deflated)
    {
        // Bounded before decoding too: the base64 is itself attacker-supplied and unbounded.
        if (base64Deflated.Length > MaxRedirectMessageBytes)
            throw new InvalidOperationException("SAML redirect-binding message exceeds the maximum accepted size");

        var compressed = Convert.FromBase64String(base64Deflated);
        using var input = new MemoryStream(compressed);
        using var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);

        // Read into a bounded buffer rather than ReadToEnd, which would materialise the whole bomb
        // before anything could object to its size.
        var buffer = new byte[MaxRedirectMessageBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = deflate.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }

        if (total > MaxRedirectMessageBytes)
            throw new InvalidOperationException("SAML redirect-binding message exceeds the maximum inflated size");

        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
