using Microsoft.AspNetCore.DataProtection;

namespace Authagonal.Bff;

/// <summary>Default <see cref="ICookieProtector"/> backed by ASP.NET Data Protection.</summary>
internal sealed class DataProtectionCookieProtector(IDataProtectionProvider provider) : ICookieProtector
{
    public string Protect(string plaintext, string purpose)
        => provider.CreateProtector("Authagonal.Bff", purpose).Protect(plaintext);

    public bool TryUnprotect(string protectedText, string purpose, out string plaintext)
    {
        try
        {
            plaintext = provider.CreateProtector("Authagonal.Bff", purpose).Unprotect(protectedText);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            plaintext = string.Empty;
            return false;
        }
    }
}
