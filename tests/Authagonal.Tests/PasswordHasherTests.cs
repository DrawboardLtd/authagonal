using System.Buffers.Binary;
using System.Security.Cryptography;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void HashPassword_ProducesPbkdf2PrefixedHash()
    {
        var hash = _hasher.HashPassword("Test1234!");
        Assert.StartsWith("PBKDF2v1$", hash);
    }

    [Fact]
    public void HashPassword_ProducesDifferentHashesForSamePassword()
    {
        var hash1 = _hasher.HashPassword("Test1234!");
        var hash2 = _hasher.HashPassword("Test1234!");
        Assert.NotEqual(hash1, hash2); // Different salts
    }

    [Fact]
    public void VerifyPassword_SucceedsForCorrectPassword()
    {
        var hash = _hasher.HashPassword("MyP@ssw0rd");
        var result = _hasher.VerifyPassword("MyP@ssw0rd", hash);
        Assert.Equal(PasswordVerifyResult.Success, result);
    }

    [Fact]
    public void VerifyPassword_FailsForWrongPassword()
    {
        var hash = _hasher.HashPassword("MyP@ssw0rd");
        var result = _hasher.VerifyPassword("WrongPassword!", hash);
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_FailsForMalformedHash()
    {
        var result = _hasher.VerifyPassword("password", "PBKDF2v1$notbase64!!!");
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_FailsForTruncatedHash()
    {
        var result = _hasher.VerifyPassword("password", "PBKDF2v1$" + Convert.ToBase64String(new byte[10]));
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_FailsForUnknownPrefix()
    {
        var result = _hasher.VerifyPassword("password", "UNKNOWN$" + Convert.ToBase64String(new byte[49]));
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_BcryptHash_ReturnsRehashNeeded()
    {
        // Pre-computed BCrypt hash for "password123"
        var bcryptHash = BCrypt.Net.BCrypt.HashPassword("password123");
        var result = _hasher.VerifyPassword("password123", bcryptHash);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyPassword_BcryptHash_FailsForWrongPassword()
    {
        var bcryptHash = BCrypt.Net.BCrypt.HashPassword("password123");
        var result = _hasher.VerifyPassword("wrongpassword", bcryptHash);
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void HashPassword_ThrowsForNull()
    {
        Assert.Throws<ArgumentNullException>(() => _hasher.HashPassword(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HashPassword_ThrowsForEmptyOrWhitespace(string password)
    {
        Assert.Throws<ArgumentException>(() => _hasher.HashPassword(password));
    }

    [Fact]
    public void VerifyPassword_ThrowsForNull()
    {
        Assert.Throws<ArgumentNullException>(() => _hasher.VerifyPassword(null!, "hash"));
        Assert.Throws<ArgumentNullException>(() => _hasher.VerifyPassword("pass", null!));
    }

    [Fact]
    public void VerifyPassword_ThrowsForEmpty()
    {
        Assert.Throws<ArgumentException>(() => _hasher.VerifyPassword("", "hash"));
    }

    // -----------------------------------------------------------------------
    // ASP.NET Identity V3 hash compatibility (used by bullclip-backend)
    // -----------------------------------------------------------------------

    [Fact]
    public void VerifyPassword_AspNetIdentityV3_Sha256_ReturnsRehashNeeded()
    {
        var hash = CreateAspNetIdentityV3Hash("Test1234!", prf: 1, iterations: 100_000);
        var result = _hasher.VerifyPassword("Test1234!", hash);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyPassword_AspNetIdentityV3_Sha512_ReturnsRehashNeeded()
    {
        var hash = CreateAspNetIdentityV3Hash("Test1234!", prf: 3, iterations: 100_000);
        var result = _hasher.VerifyPassword("Test1234!", hash);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyPassword_AspNetIdentityV3_WrongPassword_Fails()
    {
        var hash = CreateAspNetIdentityV3Hash("Test1234!", prf: 1, iterations: 100_000);
        var result = _hasher.VerifyPassword("WrongPassword!", hash);
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_AspNetIdentityV3_10kIterations_Works()
    {
        // .NET 6 default: 100k, .NET 8 default: 600k — test lower iteration count too
        var hash = CreateAspNetIdentityV3Hash("password123", prf: 1, iterations: 10_000);
        var result = _hasher.VerifyPassword("password123", hash);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, result);
    }

    /// <summary>
    /// Builds an ASP.NET Identity V3 hash blob.
    /// Format: marker(1) + prf(4 BE) + iterCount(4 BE) + saltLen(4 BE) + salt + subkey(32)
    /// </summary>
    private static string CreateAspNetIdentityV3Hash(string password, uint prf, int iterations)
    {
        var algorithm = prf switch
        {
            0 => HashAlgorithmName.SHA1,
            1 => HashAlgorithmName.SHA256,
            2 => HashAlgorithmName.SHA384,
            3 => HashAlgorithmName.SHA512,
            _ => throw new ArgumentOutOfRangeException(nameof(prf))
        };

        const int saltLength = 16;
        const int subkeyLength = 32;
        var salt = RandomNumberGenerator.GetBytes(saltLength);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, algorithm, subkeyLength);

        var output = new byte[13 + saltLength + subkeyLength];
        output[0] = 0x01; // V3 marker
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1), prf);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(5), (uint)iterations);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(9), saltLength);
        salt.CopyTo(output.AsSpan(13));
        subkey.CopyTo(output.AsSpan(13 + saltLength));

        return Convert.ToBase64String(output);
    }
}
