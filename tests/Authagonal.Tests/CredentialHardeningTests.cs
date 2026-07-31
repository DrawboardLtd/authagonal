using System.Buffers.Binary;
using System.Security.Cryptography;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// The credential-at-rest properties: a recorded work factor that can be raised, a bounded parser for
/// imported hashes, and recovery codes treated as the second-factor bypasses they are.
/// </summary>
public class CredentialHardeningTests
{
    private static PasswordHasher HasherAt(int iterations) =>
        new(Options.Create(new AuthOptions { Pbkdf2Iterations = iterations }));

    // -----------------------------------------------------------------------
    // F136 — the iteration count lives in the hash
    // -----------------------------------------------------------------------

    [Fact]
    public void HashPassword_RecordsItsIterationCount()
    {
        var hash = HasherAt(1_500).HashPassword("Test1234!");
        Assert.StartsWith("PBKDF2v2$", hash);

        var decoded = Convert.FromBase64String(hash["PBKDF2v2$".Length..]);
        Assert.Equal(0x02, decoded[0]);
        Assert.Equal(1_500, BinaryPrimitives.ReadInt32BigEndian(decoded.AsSpan(1)));
    }

    [Fact]
    public void RaisingTheIterationCount_DoesNotInvalidateExistingHashes()
    {
        // The whole defect: Auth:Pbkdf2Iterations is a documented knob whose every value change used
        // to invalidate every stored hash at once — user passwords AND client secrets — so every
        // login failed (then locked the account out) and every confidential client got
        // invalid_client with no self-service recovery.
        var hash = HasherAt(1_000).HashPassword("Test1234!");

        var raised = HasherAt(2_000);
        Assert.NotEqual(PasswordVerifyResult.Failed, raised.VerifyPassword("Test1234!", hash));
    }

    [Fact]
    public void AHashBelowTheConfiguredTarget_IsFlaggedForRehash()
    {
        // What makes raising the cost actually take effect: the upgrade-on-login path re-writes the
        // hash. Without this signal a raised setting would apply only to brand-new accounts.
        var hash = HasherAt(1_000).HashPassword("Test1234!");

        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded,
            HasherAt(2_000).VerifyPassword("Test1234!", hash));
    }

    [Fact]
    public void AHashAtTheConfiguredTarget_NeedsNoRehash()
    {
        var hasher = HasherAt(1_000);
        Assert.Equal(PasswordVerifyResult.Success,
            hasher.VerifyPassword("Test1234!", hasher.HashPassword("Test1234!")));
    }

    [Fact]
    public void LegacyV1Hashes_VerifyAtThePinnedCost_RegardlessOfConfiguration()
    {
        // v1 carried no cost, so it can only mean the 100,000 it was always written at. Verifying it
        // against current configuration is the coupling being removed.
        var v1 = BuildLegacyV1Hash("Test1234!", iterations: 100_000);

        foreach (var configured in new[] { 100_000, 600_000, 1_000_000 })
        {
            Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded,
                HasherAt(configured).VerifyPassword("Test1234!", v1));
        }

        Assert.Equal(PasswordVerifyResult.Failed, HasherAt(600_000).VerifyPassword("WrongPass1!", v1));
    }

    [Fact]
    public void DefaultIterationCount_MeetsCurrentGuidance()
    {
        Assert.Equal(600_000, new AuthOptions().Pbkdf2Iterations);
        Assert.True(new AuthOptions().Pbkdf2Iterations >= AuthOptions.MinimumPbkdf2Iterations);
    }

    // -----------------------------------------------------------------------
    // F23 — bounded parsing of imported hashes
    // -----------------------------------------------------------------------

    [Fact]
    public void ImportedIdentityV3Hash_WithAbsurdIterationCount_IsRefusedWithoutDerivingIt()
    {
        // An unbounded iterCount out of a stored blob is an anonymous CPU-exhaustion primitive: any
        // caller can POST /connect/token for that client and pin a thread-pool thread for hours in
        // uncancellable PBKDF2. Refusal must be immediate, so this test would time out if the bound
        // were removed rather than merely failing.
        var poisoned = BuildIdentityV3Hash(iterCount: int.MaxValue, saltLength: 16, subkeyLength: 32);

        var started = DateTimeOffset.UtcNow;
        Assert.Equal(PasswordVerifyResult.Failed, HasherAt(600_000).VerifyPassword("anything", poisoned));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ImportedIdentityV3Hash_WithOversizedSubkey_IsRefused()
    {
        var poisoned = BuildIdentityV3Hash(iterCount: 1_000, saltLength: 16, subkeyLength: 4096);
        Assert.Equal(PasswordVerifyResult.Failed, HasherAt(600_000).VerifyPassword("anything", poisoned));
    }

    [Fact]
    public void ImportedIdentityV3Hash_WithSaneParameters_StillVerifies()
    {
        // The bound must not break real migrated hashes.
        var real = BuildIdentityV3Hash("Test1234!", iterCount: 10_000, saltLength: 16, subkeyLength: 32);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded,
            HasherAt(600_000).VerifyPassword("Test1234!", real));
    }

    [Fact]
    public void IsRecognisedHashFormat_RejectsBlobsThisServerWouldNotHaveWritten()
    {
        Assert.True(PasswordHasher.IsRecognisedHashFormat(HasherAt(1_000).HashPassword("x")));
        Assert.True(PasswordHasher.IsRecognisedHashFormat("PBKDF2v1$abc"));
        Assert.True(PasswordHasher.IsRecognisedHashFormat("SHA256$abc"));
        Assert.True(PasswordHasher.IsRecognisedHashFormat("$2a$10$abcdefghijklmnopqrstuv"));

        Assert.False(PasswordHasher.IsRecognisedHashFormat(""));
        Assert.False(PasswordHasher.IsRecognisedHashFormat("   "));
        Assert.False(PasswordHasher.IsRecognisedHashFormat("hash-1"));
        Assert.False(PasswordHasher.IsRecognisedHashFormat(
            Convert.ToBase64String(BuildIdentityV3Bytes(int.MaxValue, 16, 32))));
    }

    // -----------------------------------------------------------------------
    // F153 / F174 — recovery codes
    // -----------------------------------------------------------------------

    [Fact]
    public void RecoveryCodes_AreSaltedNotBareSha256()
    {
        var service = CheapHasher.RecoveryCodes();
        var (codes, credentials) = service.Generate("user-1");

        var bareSha256 = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(codes[0].Replace("-", "")))).ToLowerInvariant();

        // The stored form must not be the unsalted digest — that is what made one GPU pass recover
        // every enrolled user's codes across the whole deployment at once.
        Assert.NotEqual(bareSha256, credentials[0].SecretProtected);
        Assert.True(service.VerifyCode(codes[0], credentials[0].SecretProtected!));
    }

    [Fact]
    public void RecoveryCodes_CarryFiftyBitsOfEntropy()
    {
        var (codes, _) = CheapHasher.RecoveryCodes().Generate("user-1");

        foreach (var code in codes)
            Assert.Equal(10, code.Replace("-", "").Length);
    }

    [Fact]
    public void RecoveryCodes_TwoUsersWithTheSameCode_DoNotShareADigest()
    {
        var service = CheapHasher.RecoveryCodes();

        // Distinct salts, so a precomputed table cannot be matched against every row at once.
        Assert.NotEqual(service.HashForStorage("ABCDE-23456"), service.HashForStorage("ABCDE-23456"));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>The pre-change native format: version(1) + salt(16) + key(32), no cost recorded.</summary>
    private static string BuildLegacyV1Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

        var output = new byte[1 + 16 + 32];
        output[0] = 0x01;
        salt.CopyTo(output.AsSpan(1));
        key.CopyTo(output.AsSpan(17));
        return "PBKDF2v1$" + Convert.ToBase64String(output);
    }

    private static string BuildIdentityV3Hash(int iterCount, int saltLength, int subkeyLength) =>
        Convert.ToBase64String(BuildIdentityV3Bytes(iterCount, saltLength, subkeyLength));

    private static string BuildIdentityV3Hash(string password, int iterCount, int saltLength, int subkeyLength)
    {
        var bytes = BuildIdentityV3Bytes(iterCount, saltLength, subkeyLength);
        var salt = bytes.AsSpan(13, saltLength).ToArray();
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterCount, HashAlgorithmName.SHA256, subkeyLength);
        subkey.CopyTo(bytes.AsSpan(13 + saltLength));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>marker(1) + prf(4) + iter(4) + saltLen(4) + salt + subkey, as ASP.NET Identity V3.</summary>
    private static byte[] BuildIdentityV3Bytes(int iterCount, int saltLength, int subkeyLength)
    {
        var bytes = new byte[13 + saltLength + subkeyLength];
        bytes[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), 1); // prf = SHA256
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), (uint)iterCount);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(9), (uint)saltLength);
        RandomNumberGenerator.Fill(bytes.AsSpan(13, saltLength));
        return bytes;
    }
}
