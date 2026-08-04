using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services.Saml;

/// <summary>
/// Whether a pinned IdP certificate is currently within its own validity window.
/// </summary>
/// <remarks>
/// Shared by both signature verifiers — <see cref="SamlResponseParser"/> for XML signatures and
/// <see cref="SamlRedirectBinding"/> for the query-string binding — because it was implemented for one
/// and not the other. The XML path got the check; the redirect path went straight to
/// <c>VerifyData</c> against the same trust set, so a certificate the IdP had rotated away from still
/// forced logout through <c>/saml/{connection}/logout</c> and <c>/saml/{connection}/slo</c>.
/// <para>
/// Skipping chain building and revocation is deliberate on both paths: trust comes from pinning the
/// IdP's metadata signing certificates, not from a CA. <c>NotBefore</c>/<c>NotAfter</c> are a different
/// thing — a statement the certificate makes about itself — and the only other expiry control here is
/// metadata <c>@validUntil</c>, which SAML 2.0 Metadata makes optional, several major IdPs omit, and a
/// pasted <c>MetadataXml</c> connection never re-fetches.
/// </para>
/// </remarks>
internal static class SamlCertificateValidity
{
    /// <summary>
    /// Same skew the assertion time conditions allow, so a few seconds of clock disagreement around a
    /// rollover boundary is not a hard failure.
    /// </summary>
    internal static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    internal static bool IsCurrent(X509Certificate2 cert, DateTimeOffset now, ILogger? logger = null)
    {
        if (now >= cert.NotBefore.ToUniversalTime() - Skew
            && now <= cert.NotAfter.ToUniversalTime() + Skew)
            return true;

        logger?.LogWarning(
            "Skipping IdP signing certificate {Thumbprint}: outside its validity window "
            + "({NotBefore:o} – {NotAfter:o}). Refresh the connection's metadata to pick up the rollover.",
            cert.Thumbprint, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime());

        return false;
    }
}
