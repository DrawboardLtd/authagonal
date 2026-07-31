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

        if (!IsWellFormedVerifier(codeVerifier))
            return false;

        return method switch
        {
            "S256" => ValidateS256(codeVerifier, codeChallenge),
            _ => false,
        };
    }

    /// <summary>
    /// RFC 7636 §4.1 — <c>code-verifier = 43*128unreserved</c>, where unreserved is
    /// ALPHA / DIGIT / "-" / "." / "_" / "~".
    /// </summary>
    /// <remarks>
    /// Neither half was enforced, and both matter. Without the length bound a three-character verifier
    /// is accepted whenever its SHA-256 matches the stored challenge, so the entropy PKCE depends on
    /// became whatever the client happened to pick rather than a floor the server guarantees. Without
    /// the charset check the hash below is lossy: it runs over <see cref="Encoding.ASCII"/>, which maps
    /// every non-ASCII code point to '?', so a verifier drawn from a non-ASCII alphabet collapses to a
    /// constant. Validating the charset makes that encoding lossless by construction rather than by
    /// assumption.
    /// </remarks>
    private static bool IsWellFormedVerifier(string codeVerifier)
    {
        if (codeVerifier.Length is < 43 or > 128)
            return false;

        foreach (var c in codeVerifier)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~')
                continue;
            return false;
        }

        return true;
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
