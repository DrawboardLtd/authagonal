namespace Authagonal.Bff;

/// <summary>Protects (signs + encrypts) small cookie payloads. The default implementation uses ASP.NET
/// Data Protection; replace it to key cookies from your own KMS (a hosted-seam extension point).</summary>
public interface ICookieProtector
{
    /// <summary>Protect a plaintext payload for the given purpose (purposes isolate keys per use).</summary>
    string Protect(string plaintext, string purpose);

    /// <summary>Attempt to unprotect a payload. Returns false (not throws) on any tamper/expiry/decrypt
    /// failure.</summary>
    bool TryUnprotect(string protectedText, string purpose, out string plaintext);
}
