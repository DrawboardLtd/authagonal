using System.Security.Cryptography;

namespace Authagonal.Server.Services;

public enum PasswordVerifyResult
{
    Failed = 0,
    Success = 1,
    SuccessRehashNeeded = 2
}

public sealed class PasswordHasher
{
    // PBKDF2 configuration
    private const int SaltSizeBytes = 16;       // 128-bit salt
    private const int KeySizeBytes = 32;         // 256-bit derived key
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    // Version prefix for our PBKDF2 hashes so we can evolve the format
    private const byte FormatVersion = 0x01;
    private const string Pbkdf2Prefix = "PBKDF2v1$";

    private static readonly string[] BcryptPrefixes = ["$2a$", "$2b$", "$2x$", "$2y$"];

    /// <summary>
    /// Hashes a password using PBKDF2 with SHA-256, 100k iterations, 128-bit salt, 256-bit key.
    /// Returns a string with a version prefix for future-proofing.
    /// </summary>
    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithm,
            KeySizeBytes);

        // Format: version(1) + salt(16) + key(32) = 49 bytes
        var output = new byte[1 + SaltSizeBytes + KeySizeBytes];
        output[0] = FormatVersion;
        salt.CopyTo(output.AsSpan(1));
        key.CopyTo(output.AsSpan(1 + SaltSizeBytes));

        return Pbkdf2Prefix + Convert.ToBase64String(output);
    }

    /// <summary>
    /// Verifies a password against a hash. Supports both PBKDF2 (our format) and BCrypt (legacy migration).
    /// BCrypt matches return SuccessRehashNeeded so the caller can upgrade to PBKDF2.
    /// </summary>
    public PasswordVerifyResult VerifyPassword(string password, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (IsBcryptHash(hash))
        {
            return VerifyBcrypt(password, hash);
        }

        return VerifyPbkdf2(password, hash);
    }

    private static bool IsBcryptHash(string hash)
    {
        foreach (var prefix in BcryptPrefixes)
        {
            if (hash.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static PasswordVerifyResult VerifyBcrypt(string password, string hash)
    {
        try
        {
            if (BCrypt.Net.BCrypt.Verify(password, hash))
                return PasswordVerifyResult.SuccessRehashNeeded;
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // Malformed BCrypt hash
        }

        return PasswordVerifyResult.Failed;
    }

    private static PasswordVerifyResult VerifyPbkdf2(string password, string hash)
    {
        if (!hash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
            return PasswordVerifyResult.Failed;

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(hash[Pbkdf2Prefix.Length..]);
        }
        catch (FormatException)
        {
            return PasswordVerifyResult.Failed;
        }

        if (decoded.Length < 1 + SaltSizeBytes + KeySizeBytes)
            return PasswordVerifyResult.Failed;

        var version = decoded[0];
        if (version != FormatVersion)
            return PasswordVerifyResult.Failed;

        var salt = decoded.AsSpan(1, SaltSizeBytes);
        var storedKey = decoded.AsSpan(1 + SaltSizeBytes, KeySizeBytes);

        var computedKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithm,
            KeySizeBytes);

        if (CryptographicOperations.FixedTimeEquals(computedKey, storedKey))
            return PasswordVerifyResult.Success;

        return PasswordVerifyResult.Failed;
    }
}
