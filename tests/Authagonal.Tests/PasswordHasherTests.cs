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
}
