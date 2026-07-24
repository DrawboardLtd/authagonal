using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>Round-trip and tolerance behaviour of <see cref="TotpService.Base32Decode"/>.</summary>
public class Base32DecodeTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("MY", "66")]                 // RFC 4648 test vector: "f"
    [InlineData("MZXQ", "666f")]             // "fo"
    [InlineData("MZXW6", "666f6f")]          // "foo"
    [InlineData("MZXW6YQ", "666f6f62")]      // "foob"
    [InlineData("MZXW6YTB", "666f6f6261")]   // "fooba"
    [InlineData("MZXW6YTBOI", "666f6f626172")] // "foobar"
    public void Base32Decode_MatchesRfc4648Vectors(string base32, string expectedHex)
    {
        var bytes = TotpService.Base32Decode(base32);
        Assert.Equal(expectedHex, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void Base32Decode_IsTolerantOfPaddingWhitespaceHyphensAndCase()
    {
        var canonical = TotpService.Base32Decode("MZXW6YTBOI");
        var messy = TotpService.Base32Decode(" mzxw-6ytb oi== ");
        Assert.Equal(canonical, messy);
    }

    [Fact]
    public void Base32Decode_RoundTripsEncode()
    {
        var secret = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 250, 251, 252, 253, 254, 255 };
        var encoded = TotpService.Base32Encode(secret);
        Assert.Equal(secret, TotpService.Base32Decode(encoded));
    }

    [Fact]
    public void Base32Decode_ThrowsOnInvalidCharacter()
    {
        Assert.Throws<FormatException>(() => TotpService.Base32Decode("MZXW6!YTB"));
    }
}
