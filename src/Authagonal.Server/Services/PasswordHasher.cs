using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

public enum PasswordVerifyResult
{
    Failed = 0,
    Success = 1,
    SuccessRehashNeeded = 2
}

public sealed class PasswordHasher
{
    // PBKDF2 configuration (Authagonal native format)
    private const int SaltSizeBytes = 16;       // 128-bit salt
    private const int KeySizeBytes = 32;         // 256-bit derived key
    private readonly int _iterations;
    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    // Version prefix for our PBKDF2 hashes so we can evolve the format
    private const byte FormatVersion = 0x01;
    private const string Pbkdf2Prefix = "PBKDF2v1$";

    private static readonly string[] BcryptPrefixes = ["$2a$", "$2b$", "$2x$", "$2y$"];

    // ASP.NET Identity V3 format marker
    private const byte IdentityV3Marker = 0x01;

    public PasswordHasher(IOptions<AuthOptions> authOptions)
    {
        _iterations = authOptions.Value.Pbkdf2Iterations;
    }

    public PasswordHasher() : this(Options.Create(new AuthOptions())) { }

    /// <summary>
    /// Hashes a password using PBKDF2 with SHA-256, configurable iterations, 128-bit salt, 256-bit key.
    /// Returns a string with a version prefix for future-proofing.
    /// </summary>
    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            _iterations,
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
    /// Verifies a password against a hash. Supports:
    /// <list type="bullet">
    /// <item>PBKDF2v1$ — Authagonal native format (PBKDF2-SHA256, 100k iterations)</item>
    /// <item>ASP.NET Identity V3 — base64 blob starting with 0x01 (PBKDF2-SHA256/384/512, variable iterations)</item>
    /// <item>BCrypt — hashes starting with $2a$, $2b$, $2x$, $2y$</item>
    /// </list>
    /// Non-native formats return <see cref="PasswordVerifyResult.SuccessRehashNeeded"/>
    /// so the caller can upgrade the stored hash.
    /// </summary>
    public PasswordVerifyResult VerifyPassword(string password, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (IsBcryptHash(hash))
            return VerifyBcrypt(password, hash);

        if (hash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
            return VerifyPbkdf2(password, hash);

        // Try ASP.NET Identity format (raw Base64 — no text prefix)
        return VerifyAspNetIdentity(password, hash);
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

    private PasswordVerifyResult VerifyPbkdf2(string password, string hash)
    {
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
            _iterations,
            HashAlgorithm,
            KeySizeBytes);

        if (CryptographicOperations.FixedTimeEquals(computedKey, storedKey))
            return PasswordVerifyResult.Success;

        return PasswordVerifyResult.Failed;
    }

    /// <summary>
    /// Verifies an ASP.NET Identity V3 password hash (used by Microsoft.AspNetCore.Identity).
    /// Format: marker(1) + prf(4) + iterCount(4) + saltLen(4) + salt(saltLen) + subkey(32)
    /// All multi-byte integers are big-endian.
    /// PRF values: 0=SHA1, 1=SHA256, 2=SHA384, 3=SHA512.
    /// </summary>
    private static PasswordVerifyResult VerifyAspNetIdentity(string password, string hash)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(hash);
        }
        catch (FormatException)
        {
            return PasswordVerifyResult.Failed;
        }

        // Minimum: marker(1) + prf(4) + iter(4) + saltLen(4) = 13 bytes + at least 1 byte salt + 1 byte key
        if (decoded.Length < 15)
            return PasswordVerifyResult.Failed;

        var marker = decoded[0];
        if (marker != IdentityV3Marker)
            return PasswordVerifyResult.Failed;

        var prf = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(1));
        var iterCount = (int)BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(5));
        var saltLength = (int)BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(9));

        // Sanity checks
        if (iterCount <= 0 || saltLength <= 0 || saltLength > 128)
            return PasswordVerifyResult.Failed;

        if (decoded.Length < 13 + saltLength)
            return PasswordVerifyResult.Failed;

        var salt = decoded.AsSpan(13, saltLength);
        var subkeyLength = decoded.Length - 13 - saltLength;
        if (subkeyLength <= 0)
            return PasswordVerifyResult.Failed;

        var storedSubkey = decoded.AsSpan(13 + saltLength, subkeyLength);

        var algorithm = prf switch
        {
            0 => HashAlgorithmName.SHA1,
            1 => HashAlgorithmName.SHA256,
            2 => HashAlgorithmName.SHA384,
            3 => HashAlgorithmName.SHA512,
            _ => default
        };

        if (algorithm == default)
            return PasswordVerifyResult.Failed;

        var computedSubkey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterCount,
            algorithm,
            subkeyLength);

        if (CryptographicOperations.FixedTimeEquals(computedSubkey, storedSubkey))
            return PasswordVerifyResult.SuccessRehashNeeded;

        return PasswordVerifyResult.Failed;
    }
}
