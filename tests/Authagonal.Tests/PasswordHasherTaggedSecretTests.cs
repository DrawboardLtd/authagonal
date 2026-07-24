using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// Tagged unsalted-digest client-secret formats (SHA256$/SHA512$) produced by the Duende migration.
/// </summary>
public class PasswordHasherTaggedSecretTests
{
    private readonly PasswordHasher _hasher = new();

    // base64 digests of "s3cr3t"
    private const string Sha256OfSecret = "TnOMpVY8Bs/QAYKZkz1Y2x3Yv5f2lz3Jm/bNxktVUL0=";        // 44 chars
    private const string Sha512OfSecret = "SCVRIoQR6YrYyx+LChRDyf+6/BC2MMdkbFGKsZMx6n4s8krTg1J9oQceIXevfkG551HJxPskmaoi9pgk+WVzOQ=="; // 88 chars

    [Fact]
    public void VerifyPassword_Sha256Tagged_SucceedsRehashNeeded()
    {
        var result = _hasher.VerifyPassword("s3cr3t", "SHA256$" + Sha256OfSecret);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyPassword_Sha512Tagged_SucceedsRehashNeeded()
    {
        var result = _hasher.VerifyPassword("s3cr3t", "SHA512$" + Sha512OfSecret);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyPassword_Sha256Tagged_FailsForWrongSecret()
    {
        var result = _hasher.VerifyPassword("wrong", "SHA256$" + Sha256OfSecret);
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_Sha512Tagged_FailsForWrongSecret()
    {
        var result = _hasher.VerifyPassword("wrong", "SHA512$" + Sha512OfSecret);
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_TaggedButNotBase64_Fails()
    {
        var result = _hasher.VerifyPassword("s3cr3t", "SHA256$not valid base64 !!!");
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void VerifyPassword_WrongDigestTagForBody_Fails()
    {
        // A SHA-256 body verified under the SHA512$ tag must never match.
        var result = _hasher.VerifyPassword("s3cr3t", "SHA512$" + Sha256OfSecret);
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }
}
