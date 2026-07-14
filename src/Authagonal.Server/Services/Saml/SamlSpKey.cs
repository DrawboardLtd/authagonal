using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Authagonal.Server.Services.Saml;

/// <summary>
/// F54: the SP keypair. One self-signed RSA cert per SAML connection, stored on the config as
/// base64 PKCS#12 (protected at rest by the host's secret provider). It is what lets the SP
/// decrypt EncryptedAssertions (ADFS encrypts by default once the SP metadata advertises an
/// encryption cert), sign AuthnRequests for IdPs that require it, and sign logout messages.
/// </summary>
public static class SamlSpKey
{
    /// <summary>Generate a fresh SP keypair; returns base64 PKCS#12 with no password.</summary>
    public static string CreateCertificate(string entityId)
    {
        using var rsa = RSA.Create(2048);
        // CN carries the SP entity ID (informational only — trust is by metadata exchange).
        var cn = new X500DistinguishedName($"CN={SanitizeCn(entityId)}");
        var request = new CertificateRequest(cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12));
    }

    /// <summary>Load a stored (already secret-provider-resolved) base64 PKCS#12 SP keypair.</summary>
    public static X509Certificate2 Load(string base64Pfx)
        => X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64Pfx), password: null,
            X509KeyStorageFlags.EphemeralKeySet);

    private static string SanitizeCn(string entityId)
    {
        // X500 name parsing chokes on commas/quotes; the CN is cosmetic, so strip them.
        var cleaned = entityId.Replace(",", "").Replace("\"", "").Replace("+", "").Replace(";", "");
        return string.IsNullOrWhiteSpace(cleaned) ? "Authagonal SP" : cleaned[..Math.Min(cleaned.Length, 60)];
    }
}
