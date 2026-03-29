using Authagonal.Core.Models;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

public class RecoveryCodeServiceTests
{
    private readonly RecoveryCodeService _sut = new();

    [Fact]
    public void Generate_Returns10CodesByDefault()
    {
        var (codes, credentials) = _sut.Generate("user-1");

        Assert.Equal(10, codes.Length);
        Assert.Equal(10, credentials.Length);
    }

    [Fact]
    public void Generate_CodesHaveCorrectFormat()
    {
        var (codes, _) = _sut.Generate("user-1");

        foreach (var code in codes)
        {
            // Format: XXXX-XXXX
            Assert.Matches(@"^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        }
    }

    [Fact]
    public void Generate_CredentialsAreRecoveryCodeType()
    {
        var (_, credentials) = _sut.Generate("user-1");

        foreach (var cred in credentials)
        {
            Assert.Equal(MfaCredentialType.RecoveryCode, cred.Type);
            Assert.Equal("user-1", cred.UserId);
            Assert.NotNull(cred.SecretProtected);
            Assert.False(cred.IsConsumed);
        }
    }

    [Fact]
    public void VerifyCode_MatchingCode_ReturnsTrue()
    {
        var (codes, credentials) = _sut.Generate("user-1");

        for (var i = 0; i < codes.Length; i++)
        {
            Assert.True(_sut.VerifyCode(codes[i], credentials[i].SecretProtected!));
        }
    }

    [Fact]
    public void VerifyCode_WrongCode_ReturnsFalse()
    {
        var (_, credentials) = _sut.Generate("user-1");

        Assert.False(_sut.VerifyCode("AAAA-BBBB", credentials[0].SecretProtected!));
    }

    [Fact]
    public void VerifyCode_CaseInsensitive()
    {
        var (codes, credentials) = _sut.Generate("user-1");

        // Lowercase should also work
        Assert.True(_sut.VerifyCode(codes[0].ToLowerInvariant(), credentials[0].SecretProtected!));
    }

    [Fact]
    public void VerifyCode_WithoutDash()
    {
        var (codes, credentials) = _sut.Generate("user-1");

        // Without dash should also work
        var codeWithoutDash = codes[0].Replace("-", "");
        Assert.True(_sut.VerifyCode(codeWithoutDash, credentials[0].SecretProtected!));
    }

    [Fact]
    public void Generate_CodesAreUnique()
    {
        var (codes, _) = _sut.Generate("user-1");

        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    [Fact]
    public void Generate_CustomCount()
    {
        var (codes, credentials) = _sut.Generate("user-1", count: 5);

        Assert.Equal(5, codes.Length);
        Assert.Equal(5, credentials.Length);
    }
}
