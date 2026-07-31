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

    /// plain is no longer accepted, even for a matching pair. It offers nothing against the attack PKCE
    /// exists for — the challenge IS the verifier, so whoever can read the authorization request can
    /// redeem the code — and discovery has only ever advertised S256.
    [Fact]
    public void ValidateCodeVerifier_Plain_IsRejectedEvenWhenItMatches()
    {
        var value = "my-plain-code-verifier";
        Assert.False(PkceValidator.ValidateCodeVerifier(value, value, "plain"));
    }

    /// RFC 7636 §4.3 makes a missing method mean plain, so silence must fail rather than downgrade.
    [Fact]
    public void ValidateCodeVerifier_MissingMethod_DoesNotDowngradeToPlain()
    {
        var value = "my-plain-code-verifier";
        Assert.False(PkceValidator.ValidateCodeVerifier(value, value, null));
        Assert.False(PkceValidator.ValidateCodeVerifier(value, value, ""));
    }

    [Fact]
    public void ValidateCodeVerifier_UnsupportedMethod_ReturnsFalse()
    {
        // Fails closed rather than throwing: an odd stored code should be a rejected grant, not a 500.
        Assert.False(PkceValidator.ValidateCodeVerifier("v", "c", "RS256"));
    }

    [Fact]
    public void ValidateCodeVerifier_MissingVerifierOrChallenge_ReturnsFalse()
    {
        Assert.False(PkceValidator.ValidateCodeVerifier(null, "challenge", "S256"));
        Assert.False(PkceValidator.ValidateCodeVerifier("verifier", null, "S256"));
        Assert.False(PkceValidator.ValidateCodeVerifier("", "challenge", "S256"));
    }

    // ---------------------------------------------------------------------------------------------
    // F332 — RFC 7636 §4.1: code-verifier = 43*128unreserved
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A verifier shorter than 43 characters must be refused even when it genuinely hashes to the
    /// stored challenge. Otherwise the entropy PKCE rests on is whatever the client picked, and a
    /// three-character verifier is brute-forceable against an intercepted code.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(42)] // one short of the floor
    public void ValidateCodeVerifier_TooShort_IsRejectedEvenWhenItMatches(int length)
    {
        var verifier = new string('a', length);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.False(PkceValidator.ValidateCodeVerifier(verifier, challenge, "S256"));
    }

    [Fact]
    public void ValidateCodeVerifier_TooLong_IsRejectedEvenWhenItMatches()
    {
        var verifier = new string('a', 129);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.False(PkceValidator.ValidateCodeVerifier(verifier, challenge, "S256"));
    }

    [Fact]
    public void ValidateCodeVerifier_MinimumAndMaximumLengths_AreAccepted()
    {
        foreach (var length in new[] { 43, 128 })
        {
            var verifier = new string('a', length);
            var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            Assert.True(PkceValidator.ValidateCodeVerifier(verifier, challenge, "S256"),
                $"a {length}-character verifier is inside the ABNF and must be accepted");
        }
    }

    /// <summary>
    /// The charset half is not cosmetic. The hash runs over ASCII, which maps every non-ASCII code
    /// point to '?', so without this check two different non-ASCII verifiers of the same length hash
    /// identically — one redeems the other's code.
    /// </summary>
    [Fact]
    public void ValidateCodeVerifier_NonAsciiAlphabet_DoesNotCollapseToAConstant()
    {
        var attacker = new string('é', 43);
        var victim = new string('ü', 43);
        var victimChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(victim)));

        // Same challenge under the lossy encoding — the collision is real, so the charset check is
        // what stands between it and a redeemable code.
        Assert.Equal(victimChallenge, Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(attacker))));
        Assert.False(PkceValidator.ValidateCodeVerifier(attacker, victimChallenge, "S256"));
        Assert.False(PkceValidator.ValidateCodeVerifier(victim, victimChallenge, "S256"));
    }

    [Theory]
    [InlineData('+')]
    [InlineData('/')]
    [InlineData('=')]
    [InlineData(' ')]
    [InlineData('%')]
    public void ValidateCodeVerifier_ReservedCharacter_IsRejected(char reserved)
    {
        var verifier = new string('a', 42) + reserved;
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.False(PkceValidator.ValidateCodeVerifier(verifier, challenge, "S256"));
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
