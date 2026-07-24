using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>The exposed <see cref="RecoveryCodeService.HashForStorage"/> must match the store's own pipeline.</summary>
public class RecoveryCodeHashForStorageTests
{
    private readonly RecoveryCodeService _service = new();

    [Fact]
    public void HashForStorage_VerifiesWithVerifyCode()
    {
        var stored = _service.HashForStorage("ABCD-2345");
        Assert.True(_service.VerifyCode("ABCD-2345", stored));
    }

    [Fact]
    public void HashForStorage_IsNormalizationInsensitive()
    {
        // Hyphens/whitespace/case are normalized away, so these all hash identically.
        var a = _service.HashForStorage("ABCD-2345");
        var b = _service.HashForStorage("abcd2345");
        var c = _service.HashForStorage("  ABCD 2345 ");
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void HashForStorage_DiffersForDifferentCodes()
    {
        Assert.NotEqual(_service.HashForStorage("ABCD-2345"), _service.HashForStorage("WXYZ-6789"));
    }
}
