using System.Security.Cryptography;
using System.Text;

namespace Authagonal.Protocol.Services;

public static class PkceValidator
{
    /// <summary>
    /// Verifies a code_verifier against a stored challenge. S256 only.
    /// </summary>
    /// <remarks>
    /// <c>plain</c> is in RFC 7636 but provides no protection against the attack PKCE exists for: the
    /// challenge IS the verifier, so anyone who can read the authorization request can redeem an
    /// intercepted code. It is also the RFC's default when the method is omitted, so accepting it meant
    /// a challenge sent without a method silently got the weakest form. Anything that is not S256 returns
    /// false rather than throwing, so a code stored by any other path fails closed instead of 500ing.
    /// </remarks>
    public static bool ValidateCodeVerifier(string? codeVerifier, string? codeChallenge, string? method)
    {
        if (string.IsNullOrWhiteSpace(codeVerifier) || string.IsNullOrWhiteSpace(codeChallenge))
            return false;

        return method switch
        {
            "S256" => ValidateS256(codeVerifier, codeChallenge),
            _ => false,
        };
    }

    private static bool ValidateS256(string codeVerifier, string codeChallenge)
    {
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var computedChallenge = Base64UrlEncode(challengeBytes);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computedChallenge),
            Encoding.ASCII.GetBytes(codeChallenge));
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
