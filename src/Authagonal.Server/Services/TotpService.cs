using System.Security.Cryptography;

namespace Authagonal.Server.Services;

public sealed class TotpService
{
    private const int SecretLength = 20;
    private const int CodeDigits = 6;
    private const int TimeStepSeconds = 30;
    private static readonly int Modulo = (int)Math.Pow(10, CodeDigits);

    public byte[] GenerateSecret()
    {
        return RandomNumberGenerator.GetBytes(SecretLength);
    }

    public string GenerateCode(byte[] secret, long? timeStep = null)
    {
        var step = timeStep ?? GetCurrentTimeStep();
        var stepBytes = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(stepBytes);

        var hmac = HMACSHA1.HashData(secret, stepBytes);
        var offset = hmac[^1] & 0x0F;
        var code = ((hmac[offset] & 0x7F) << 24)
                 | ((hmac[offset + 1] & 0xFF) << 16)
                 | ((hmac[offset + 2] & 0xFF) << 8)
                 | (hmac[offset + 3] & 0xFF);

        return (code % Modulo).ToString().PadLeft(CodeDigits, '0');
    }

    public bool VerifyCode(byte[] secret, string code, int window = 1)
        => GetMatchingStep(secret, code, window: window) is not null;

    /// <summary>
    /// Returns the time-step the code matched at (within ±<paramref name="window"/>), or null if it
    /// doesn't match. Steps at or below <paramref name="minExclusiveStep"/> are skipped, so a caller
    /// that records the last-accepted step can reject replays of an already-used code within its
    /// validity window.
    /// </summary>
    public long? GetMatchingStep(byte[] secret, string code, long minExclusiveStep = long.MinValue, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != CodeDigits)
            return null;

        var currentStep = GetCurrentTimeStep();
        for (var i = -window; i <= window; i++)
        {
            var step = currentStep + i;
            if (step <= minExclusiveStep)
                continue;

            var expected = GenerateCode(secret, step);
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    public string GetOtpAuthUri(string email, byte[] secret, string issuer = "Authagonal")
    {
        var base32Secret = Base32Encode(secret);
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedEmail = Uri.EscapeDataString(email);
        return $"otpauth://totp/{encodedIssuer}:{encodedEmail}?secret={base32Secret}&issuer={encodedIssuer}&algorithm=SHA1&digits={CodeDigits}&period={TimeStepSeconds}";
    }

    private static long GetCurrentTimeStep()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TimeStepSeconds;
    }

    /// <summary>
    /// Decodes an RFC 4648 base32 string to bytes. Tolerant: ignores padding (<c>=</c>),
    /// whitespace and hyphens, and is case-insensitive — Duende stored TOTP AuthenticatorKeys as
    /// base32 that may carry any of these. Throws <see cref="FormatException"/> on an
    /// out-of-alphabet character.
    /// </summary>
    public static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        if (string.IsNullOrEmpty(input))
            return [];

        int buffer = 0, bitsLeft = 0;
        var output = new List<byte>(input.Length * 5 / 8 + 1);

        foreach (var raw in input)
        {
            if (raw is '=' or '-' || char.IsWhiteSpace(raw))
                continue;

            var index = alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0)
                throw new FormatException($"Invalid base32 character '{raw}'.");

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return [.. output];
    }

    public static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new char[(data.Length * 8 + 4) / 5];
        var index = 0;
        int buffer = 0, bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                result[index++] = alphabet[(buffer >> bitsLeft) & 0x1F];
            }
        }

        if (bitsLeft > 0)
            result[index++] = alphabet[(buffer << (5 - bitsLeft)) & 0x1F];

        return new string(result, 0, index);
    }
}
