using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;

namespace Authagonal.Server.Services;

public sealed class RecoveryCodeService
{
    private const int CodeLength = 8;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1

    public (string[] PlaintextCodes, MfaCredential[] Credentials) Generate(string userId, int count = 10)
    {
        var codes = new string[count];
        var credentials = new MfaCredential[count];

        for (var i = 0; i < count; i++)
        {
            var code = GenerateCode();
            codes[i] = $"{code[..4]}-{code[4..]}";
            credentials[i] = new MfaCredential
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Type = MfaCredentialType.RecoveryCode,
                Name = $"Recovery code {i + 1}",
                SecretProtected = HashCode(codes[i]),
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }

        return (codes, credentials);
    }

    public bool VerifyCode(string code, string storedHash)
    {
        var normalized = NormalizeCode(code);
        var hash = HashNormalized(normalized);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(storedHash));
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    private static string HashCode(string code)
    {
        var normalized = NormalizeCode(code);
        return HashNormalized(normalized);
    }

    private static string NormalizeCode(string code)
    {
        return code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
    }

    private static string HashNormalized(string normalized)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
