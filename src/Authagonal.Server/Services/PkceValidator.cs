using System.Security.Cryptography;
using System.Text;

namespace Authagonal.Server.Services;

public static class PkceValidator
{
    /// <summary>
    /// Validates a PKCE code verifier against the stored code challenge.
    /// </summary>
    /// <param name="codeVerifier">The code_verifier sent by the client in the token request.</param>
    /// <param name="codeChallenge">The code_challenge stored during authorization.</param>
    /// <param name="method">The code_challenge_method: "S256" or "plain".</param>
    /// <returns>True if the verifier matches the challenge; false otherwise.</returns>
    public static bool ValidateCodeVerifier(string codeVerifier, string codeChallenge, string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);

        return method switch
        {
            "S256" => ValidateS256(codeVerifier, codeChallenge),
            "plain" => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(codeVerifier),
                Encoding.ASCII.GetBytes(codeChallenge)),
            _ => throw new ArgumentException($"Unsupported PKCE code challenge method: {method}", nameof(method))
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
