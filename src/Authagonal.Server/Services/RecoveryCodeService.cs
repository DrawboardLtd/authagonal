using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;

namespace Authagonal.Server.Services;

/// <summary>
/// Generates and verifies MFA recovery codes — each of which is a standalone second-factor bypass,
/// so they are treated as credentials of the same weight as a password.
/// </summary>
/// <remarks>
/// They were not. Codes were 8 characters from a 32-symbol alphabet (exactly 40 bits) stored as a
/// single unsalted round of SHA-256. 2^40 unsalted SHA-256 evaluations is under a minute on one
/// commodity GPU, and because there was no salt the pass was not per-user: an attacker built the
/// digest set once and matched it against every row at once, recovering the live recovery codes of
/// every enrolled user in the deployment simultaneously. A store read that would otherwise cost
/// months of PBKDF2 against passwords instead neutralised the second factor for the entire user base.
/// <para>
/// The documented mitigation was encryption at rest via <see cref="Authagonal.Core.Services.ISecretProvider"/>,
/// but that defaults to <c>PlaintextSecretProvider</c> whenever <c>SecretProvider:VaultUri</c> is
/// unset — and there is no such setting at all on the self-hosted SQL path, which registers no secret
/// provider. So the mitigation was absent by default and unavailable on a shipped backend.
/// </para>
/// <para>
/// Now: 10 characters (50 bits) hashed with the same salted PBKDF2 KDF as passwords. The legacy
/// SHA-256 form still verifies so existing codes keep working until regenerated.
/// </para>
/// </remarks>
public sealed class RecoveryCodeService(PasswordHasher passwordHasher)
{
    /// <summary>
    /// 10 symbols from a 32-symbol alphabet — 50 bits. The KDF is what makes a store leak expensive;
    /// the extra entropy is what keeps an ONLINE guess hopeless without relying on rate limiting.
    /// </summary>
    private const int CodeLength = 10;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1

    /// <summary>Legacy stored form: 64 lowercase hex characters of unsalted SHA-256.</summary>
    private const int LegacySha256HexLength = 64;

    /// <summary>Parameterless construction keeps hand-built hosts and the migration CLI working.</summary>
    public RecoveryCodeService() : this(new PasswordHasher()) { }

    public (string[] PlaintextCodes, MfaCredential[] Credentials) Generate(string userId, int count = 10)
    {
        var codes = new string[count];
        var credentials = new MfaCredential[count];

        for (var i = 0; i < count; i++)
        {
            var code = GenerateCode();
            codes[i] = $"{code[..5]}-{code[5..]}";
            credentials[i] = new MfaCredential
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Type = MfaCredentialType.RecoveryCode,
                Name = $"Recovery code {i + 1}",
                SecretProtected = HashForStorage(codes[i]),
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }

        return (codes, credentials);
    }

    /// <summary>
    /// Produces the stored-hash form of a recovery code, the same pipeline <see cref="Generate"/> and
    /// <see cref="VerifyCode"/> use. Exposed so migration code can persist recovery codes lifted from
    /// another system in this store's native format.
    /// </summary>
    public string HashForStorage(string code) => passwordHasher.HashPassword(NormalizeCode(code));

    public bool VerifyCode(string code, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash)) return false;

        var normalized = NormalizeCode(code);
        if (normalized.Length == 0) return false;

        // Codes written before this change. Kept so an existing user's printed codes keep working;
        // regenerating replaces them with the KDF form. Still constant-time, as before.
        if (IsLegacySha256(storedHash))
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(LegacyHash(normalized)),
                Encoding.UTF8.GetBytes(storedHash));
        }

        return passwordHasher.VerifyPassword(normalized, storedHash) != PasswordVerifyResult.Failed;
    }

    /// <summary>
    /// True for the legacy unsalted-SHA-256 form. Checked by shape rather than by a prefix because
    /// the old format carried none — which is also why it cannot be told apart from a hash of any
    /// other kind except by length and alphabet.
    /// </summary>
    private static bool IsLegacySha256(string storedHash)
    {
        if (storedHash.Length != LegacySha256HexLength) return false;

        foreach (var c in storedHash)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex) return false;
        }

        return true;
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    private static string NormalizeCode(string code)
    {
        return code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
    }

    private static string LegacyHash(string normalized)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
