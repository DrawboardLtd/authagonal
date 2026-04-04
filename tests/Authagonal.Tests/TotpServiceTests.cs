using Authagonal.Server.Services;

namespace Authagonal.Tests;

public class TotpServiceTests
{
    private readonly TotpService _sut = new();

    [Fact]
    public void GenerateSecret_Returns20Bytes()
    {
        var secret = _sut.GenerateSecret();
        Assert.Equal(20, secret.Length);
    }

    [Fact]
    public void GenerateCode_Returns6DigitString()
    {
        var secret = _sut.GenerateSecret();
        var code = _sut.GenerateCode(secret);

        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsDigit));
    }

    [Fact]
    public void VerifyCode_ValidCode_ReturnsTrue()
    {
        var secret = _sut.GenerateSecret();
        var code = _sut.GenerateCode(secret);

        Assert.True(_sut.VerifyCode(secret, code));
    }

    [Fact]
    public void VerifyCode_WrongCode_ReturnsFalse()
    {
        var secret = _sut.GenerateSecret();
        Assert.False(_sut.VerifyCode(secret, "000000"));
    }

    [Fact]
    public void VerifyCode_NullOrEmpty_ReturnsFalse()
    {
        var secret = _sut.GenerateSecret();
        Assert.False(_sut.VerifyCode(secret, ""));
        Assert.False(_sut.VerifyCode(secret, null!));
    }

    [Fact]
    public void VerifyCode_WrongLength_ReturnsFalse()
    {
        var secret = _sut.GenerateSecret();
        Assert.False(_sut.VerifyCode(secret, "12345"));
        Assert.False(_sut.VerifyCode(secret, "1234567"));
    }

    [Fact]
    public void VerifyCode_AdjacentTimeStep_ReturnsTrue()
    {
        var secret = _sut.GenerateSecret();
        // Code from one step ago should still be valid with window=1
        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var pastCode = _sut.GenerateCode(secret, currentStep - 1);
        Assert.True(_sut.VerifyCode(secret, pastCode, window: 1));
    }

    [Fact]
    public void VerifyCode_FarFutureStep_ReturnsFalse()
    {
        var secret = _sut.GenerateSecret();
        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var farCode = _sut.GenerateCode(secret, currentStep + 10);
        Assert.False(_sut.VerifyCode(secret, farCode, window: 1));
    }

    [Fact]
    public void GenerateCode_DifferentSecrets_DifferentCodes()
    {
        var secret1 = _sut.GenerateSecret();
        var secret2 = _sut.GenerateSecret();
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        var code1 = _sut.GenerateCode(secret1, step);
        var code2 = _sut.GenerateCode(secret2, step);

        // Not guaranteed but extremely unlikely to be equal
        Assert.NotEqual(code1, code2);
    }

    [Fact]
    public void GetOtpAuthUri_ContainsExpectedComponents()
    {
        var secret = _sut.GenerateSecret();
        var uri = _sut.GetOtpAuthUri("user@example.com", secret, "TestIssuer");

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("TestIssuer", uri);
        Assert.Contains("user%40example.com", uri);
        Assert.Contains("secret=", uri);
        Assert.Contains("algorithm=SHA1", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }
}
