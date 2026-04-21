using System.Security.Cryptography;
using System.Text;
using Authagonal.Protocol.Services;

namespace Authagonal.Tests;

public class PkceValidatorTests
{
    [Fact]
    public void ValidateCodeVerifier_S256_ValidPair_ReturnsTrue()
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        // Compute the expected challenge: BASE64URL(SHA256(verifier))
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.True(PkceValidator.ValidateCodeVerifier(verifier, challenge, "S256"));
    }

    [Fact]
    public void ValidateCodeVerifier_S256_WrongVerifier_ReturnsFalse()
    {
        var verifier = "correct-verifier-value-here-1234567890";
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.False(PkceValidator.ValidateCodeVerifier("wrong-verifier", challenge, "S256"));
    }

    [Fact]
    public void ValidateCodeVerifier_Plain_MatchingPair_ReturnsTrue()
    {
        var value = "my-plain-code-verifier";
        Assert.True(PkceValidator.ValidateCodeVerifier(value, value, "plain"));
    }

    [Fact]
    public void ValidateCodeVerifier_Plain_Mismatch_ReturnsFalse()
    {
        Assert.False(PkceValidator.ValidateCodeVerifier("verifier", "different", "plain"));
    }

    [Fact]
    public void ValidateCodeVerifier_Plain_IsCaseSensitive()
    {
        Assert.False(PkceValidator.ValidateCodeVerifier("Verifier", "verifier", "plain"));
    }

    [Fact]
    public void ValidateCodeVerifier_UnsupportedMethod_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            PkceValidator.ValidateCodeVerifier("v", "c", "RS256"));
    }

    [Fact]
    public void ValidateCodeVerifier_NullVerifier_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PkceValidator.ValidateCodeVerifier(null!, "challenge", "S256"));
    }

    [Fact]
    public void ValidateCodeVerifier_NullChallenge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PkceValidator.ValidateCodeVerifier("verifier", null!, "S256"));
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
