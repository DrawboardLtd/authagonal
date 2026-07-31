using Authagonal.Core.Models;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

public class RecoveryCodeServiceTests
{
    private readonly RecoveryCodeService _sut = Infrastructure.CheapHasher.RecoveryCodes();

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
            // Format: XXXXX-XXXXX. Ten symbols from a 32-symbol alphabet is 50 bits; the previous
            // eight gave exactly 40, which is a few GPU-seconds of exhaustive search.
            Assert.Matches(@"^[A-Z2-9]{5}-[A-Z2-9]{5}$", code);
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

    // F35 — the stored value is encrypted at rest (mirrors how the endpoint wraps it via the secret
    // provider). A storage dump then yields ciphertext, not a brute-forceable hash; the code still
    // verifies after the endpoint resolves the value, and pre-encryption plaintext hashes still verify.
    [Fact]
    public async Task VerifyCode_AfterAtRestEncryption_StillVerifies_AndLegacyPlaintextToo()
    {
        var provider = new EncryptingSecretProvider();
        var (codes, credentials) = _sut.Generate("user-1");

        // Simulate the endpoint's at-rest protection of the hash.
        var atRest = await provider.ProtectAsync("mfa-recovery-user-1", credentials[0].SecretProtected!);
        Assert.StartsWith(EncryptingSecretProvider.Prefix, atRest);          // ciphertext, not the raw hash
        Assert.DoesNotContain(credentials[0].SecretProtected!, atRest);      // the hash isn't sitting in the clear

        // Verify path resolves before comparing.
        var resolved = await provider.ResolveAsync(atRest);
        Assert.True(_sut.VerifyCode(codes[0], resolved));

        // Legacy rows (unencrypted hash) pass through Resolve unchanged and still verify.
        var legacy = await provider.ResolveAsync(credentials[0].SecretProtected!);
        Assert.True(_sut.VerifyCode(codes[0], legacy));
    }

    // Reversible prefix-tagged fake mirroring the ISecretProvider contract (vault: envelope + passthrough).
    private sealed class EncryptingSecretProvider : Authagonal.Core.Services.ISecretProvider
    {
        public const string Prefix = "vault:v1:";
        public Task<string> ProtectAsync(string purpose, string plaintext, CancellationToken ct = default)
            => Task.FromResult(Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)));
        public Task<string> ResolveAsync(string stored, CancellationToken ct = default)
            => Task.FromResult(stored.StartsWith(Prefix, StringComparison.Ordinal)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(stored[Prefix.Length..]))
                : stored);
    }
}
