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
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != CodeDigits)
            return false;

        var currentStep = GetCurrentTimeStep();
        for (var i = -window; i <= window; i++)
        {
            var expected = GenerateCode(secret, currentStep + i);
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
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
