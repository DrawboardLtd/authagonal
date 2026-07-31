using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>The exposed <see cref="RecoveryCodeService.HashForStorage"/> must match the store's own pipeline.</summary>
public class RecoveryCodeHashForStorageTests
{
    private readonly RecoveryCodeService _service = Infrastructure.CheapHasher.RecoveryCodes();

    [Fact]
    public void HashForStorage_VerifiesWithVerifyCode()
    {
        var stored = _service.HashForStorage("ABCD-2345");
        Assert.True(_service.VerifyCode("ABCD-2345", stored));
    }

    [Fact]
    public void HashForStorage_IsNormalizationInsensitive()
    {
        // Hyphens/whitespace/case are normalized away, so any of these spellings verifies against a
        // hash of any other. Asserted through VerifyCode rather than by comparing stored values:
        // equal hashes for equal input is precisely the unsalted property that let one GPU pass
        // recover every user's codes at once.
        var stored = _service.HashForStorage("ABCD-23456");

        Assert.True(_service.VerifyCode("ABCD-23456", stored));
        Assert.True(_service.VerifyCode("abcd23456", stored));
        Assert.True(_service.VerifyCode("  ABCD 23456 ", stored));
    }

    [Fact]
    public void HashForStorage_IsSalted_SoTheSameCodeStoresDifferently()
    {
        // Two credentials holding the same code must not share a digest — otherwise a precomputed
        // table matches every row in the deployment simultaneously.
        Assert.NotEqual(_service.HashForStorage("ABCD-23456"), _service.HashForStorage("ABCD-23456"));
    }

    [Fact]
    public void HashForStorage_DiffersForDifferentCodes()
    {
        Assert.NotEqual(_service.HashForStorage("ABCD-23456"), _service.HashForStorage("WXYZ-67892"));
    }

    [Fact]
    public void VerifyCode_StillAcceptsLegacyUnsaltedSha256()
    {
        // Codes printed before this change must keep working until the user regenerates.
        var legacy = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("ABCD2345"))).ToLowerInvariant();

        Assert.True(_service.VerifyCode("ABCD-2345", legacy));
        Assert.False(_service.VerifyCode("WXYZ-6789", legacy));
    }
}
