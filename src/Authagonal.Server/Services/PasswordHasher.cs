using System.Buffers.Binary;
using System.Security.Cryptography;
using Authagonal.Core.Services;
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

    /// <summary>
    /// Current format: <c>version(1) + iterations(4, big-endian) + salt(16) + key(32)</c>.
    /// </summary>
    /// <remarks>
    /// v1 did not record the iteration count, so verification re-derived at whatever
    /// <c>Auth:Pbkdf2Iterations</c> currently said. That made the documented knob unusable: changing
    /// it invalidated every stored hash in the deployment at once — user passwords AND client
    /// secrets, which share this code — so every password login failed (and then locked the account
    /// out after five attempts) and every confidential client got invalid_client with no
    /// self-service recovery. The work factor was therefore frozen for the life of the deployment,
    /// which is why the default sat six times below current OWASP guidance.
    /// </remarks>
    private const byte FormatVersion2 = 0x02;
    private const string Pbkdf2V2Prefix = "PBKDF2v2$";

    /// <summary>
    /// The cost <c>PBKDF2v1$</c> hashes were actually derived at. Pinned, not read from
    /// configuration, because that coupling is the defect: a v1 hash's cost is a fact about the
    /// stored bytes, not a current setting.
    /// </summary>
    private const int LegacyPbkdf2Iterations = 100_000;

    /// <summary>
    /// Ceiling on an iteration count read out of a stored ASP.NET Identity V3 blob, and on the
    /// subkey length derived from it.
    /// </summary>
    /// <remarks>
    /// Both were taken from the blob with no upper bound and handed to <c>Rfc2898DeriveBytes.Pbkdf2</c>,
    /// which is CPU-bound, uncancellable and takes no timeout. Stored hashes are not purely
    /// server-generated — the admin client API accepts <c>ClientSecretHashes</c> verbatim — so a
    /// crafted blob declaring 2^31-1 iterations turned every anonymous <c>/connect/token</c> call for
    /// that client into hours of pinned CPU on a thread-pool thread. A dozen such requests take the
    /// identity provider down for every tenant.
    /// </remarks>
    private const int MaxImportedIterations = 1_000_000;
    private const int MaxImportedSubkeyLength = 256;

    // Tagged unsalted-digest formats used by Duende-migrated CLIENT SECRETS (not user passwords).
    // Duende stored client secrets as a bare base64 SHA-256/512 digest of the UTF-8 secret; the
    // migration tags them so this verifier knows which digest to recompute.
    private const string Sha256Prefix = "SHA256$";
    private const string Sha512Prefix = "SHA512$";

    private static readonly string[] BcryptPrefixes = ["$2a$", "$2b$", "$2x$", "$2y$"];

    // ASP.NET Identity V3 format marker
    private const byte IdentityV3Marker = 0x01;

    /// <remarks>
    /// The configured cost is used as given. The <see cref="AuthOptions.MinimumPbkdf2Iterations"/>
    /// floor is enforced where configuration is bound (see <c>AuthagonalExtensions</c>) rather than
    /// here, so validating a deployment's settings stays a composition-time concern and a test or a
    /// tool can still construct a deliberately cheap hasher.
    /// </remarks>
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

        // Format: version(1) + iterations(4, BE) + salt(16) + key(32) = 53 bytes. The cost is recorded
        // so verification never has to guess it from current configuration.
        var output = new byte[1 + 4 + SaltSizeBytes + KeySizeBytes];
        output[0] = FormatVersion2;
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(1), _iterations);
        salt.CopyTo(output.AsSpan(1 + 4));
        key.CopyTo(output.AsSpan(1 + 4 + SaltSizeBytes));

        return Pbkdf2V2Prefix + Convert.ToBase64String(output);
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

        // Routed on the PREFIX, not on validity: a bcrypt-prefixed blob is bcrypt's business even when it is
        // malformed, and VerifyBcrypt refuses it. Routing on validity instead would drop a malformed one
        // through to the unprefixed ASP.NET Identity branch at the end of this method, where the parameters
        // driving PBKDF2's cost come from the blob itself — which is the fall-through IsRecognisedHashFormat
        // exists to prevent.
        if (BcryptHashFormat.HasBcryptPrefix(hash))
            return VerifyBcrypt(password, hash);

        if (hash.StartsWith(Pbkdf2V2Prefix, StringComparison.Ordinal))
            return VerifyPbkdf2V2(password, hash);

        if (hash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
            return VerifyPbkdf2(password, hash);

        // Tagged unsalted digests — Duende-migrated client secrets only (see Sha256Prefix note).
        if (hash.StartsWith(Sha256Prefix, StringComparison.Ordinal))
            return VerifyTaggedDigest(password, hash[Sha256Prefix.Length..], sha512: false);

        if (hash.StartsWith(Sha512Prefix, StringComparison.Ordinal))
            return VerifyTaggedDigest(password, hash[Sha512Prefix.Length..], sha512: true);

        // Try ASP.NET Identity format (raw Base64 — no text prefix)
        return VerifyAspNetIdentity(password, hash);
    }

    /// <summary>
    /// True when <paramref name="hash"/> carries a prefix this verifier recognises. Callers that
    /// accept stored hashes from outside (the admin client API) use this to refuse blobs that would
    /// otherwise fall through to the unprefixed ASP.NET Identity path, where the parameters driving
    /// PBKDF2's cost come from the blob itself.
    /// </summary>
    public static bool IsRecognisedHashFormat(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return false;

        return IsBcryptHash(hash)
            || hash.StartsWith(Pbkdf2V2Prefix, StringComparison.Ordinal)
            || hash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal)
            || hash.StartsWith(Sha256Prefix, StringComparison.Ordinal)
            || hash.StartsWith(Sha512Prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// True for the bare unsalted digest formats the Duende migration imports
    /// (<c>SHA256$</c>/<c>SHA512$</c>) — verifiable, but recoverable from a store dump by rainbow
    /// table, so worth naming out loud.
    /// </summary>
    public static bool IsUnsaltedDigestHash(string hash) =>
        !string.IsNullOrWhiteSpace(hash)
        && (hash.StartsWith(Sha256Prefix, StringComparison.Ordinal)
            || hash.StartsWith(Sha512Prefix, StringComparison.Ordinal));

    /// <summary>
    /// True when <paramref name="hash"/> is a bcrypt hash this server will verify — structurally valid and
    /// within the cost bound. See <see cref="BcryptHashFormat"/>, which both bcrypt verifiers share.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="IsRecognisedHashFormat"/>, so the admin API refuses an over-cost or malformed bcrypt
    /// entry at the write rather than storing a permanent denial of service for that client.
    /// </remarks>
    private static bool IsBcryptHash(string hash) => BcryptHashFormat.IsValid(hash);

    private static PasswordVerifyResult VerifyBcrypt(string password, string hash)
    {
        // Re-checked at verification, not only at the admin-API boundary: a hash stored before this bound
        // existed, or written by a path that does not validate (a Duende-migrated user row), must fail closed
        // rather than burn the CPU or throw.
        if (!BcryptHashFormat.IsValid(hash))
            return PasswordVerifyResult.Failed;

        try
        {
            if (BCrypt.Net.BCrypt.Verify(password, hash))
                return PasswordVerifyResult.SuccessRehashNeeded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any parse failure means this hash does not verify — never that the request should fault. The
            // catch is deliberately broad because the structural check above cannot anticipate every shape a
            // future library version rejects differently, and the cost of guessing wrong is a permanent 500 on
            // a principal's every authentication.
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

        // The pinned legacy cost, NOT the configured one — see LegacyPbkdf2Iterations.
        var computedKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            LegacyPbkdf2Iterations,
            HashAlgorithm,
            KeySizeBytes);

        // Always a rehash: a v1 hash carries no cost, so re-storing it as v2 is what lets the work
        // factor ever be raised. The caller's upgrade-on-login path (AuthEndpoints) does the write.
        if (CryptographicOperations.FixedTimeEquals(computedKey, storedKey))
            return PasswordVerifyResult.SuccessRehashNeeded;

        return PasswordVerifyResult.Failed;
    }

    /// <summary>
    /// Verifies the current format, deriving at the cost recorded IN the hash and signalling a rehash
    /// whenever that cost is below the configured target.
    /// </summary>
    private PasswordVerifyResult VerifyPbkdf2V2(string password, string hash)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(hash[Pbkdf2V2Prefix.Length..]);
        }
        catch (FormatException)
        {
            return PasswordVerifyResult.Failed;
        }

        if (decoded.Length < 1 + 4 + SaltSizeBytes + KeySizeBytes)
            return PasswordVerifyResult.Failed;

        if (decoded[0] != FormatVersion2)
            return PasswordVerifyResult.Failed;

        var iterations = BinaryPrimitives.ReadInt32BigEndian(decoded.AsSpan(1));

        // Bounded for the same reason the imported-hash path is: this value drives an uncancellable
        // CPU-bound derivation reachable from an anonymous request.
        if (iterations <= 0 || iterations > MaxImportedIterations)
            return PasswordVerifyResult.Failed;

        var salt = decoded.AsSpan(1 + 4, SaltSizeBytes);
        var storedKey = decoded.AsSpan(1 + 4 + SaltSizeBytes, KeySizeBytes);

        var computedKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithm,
            KeySizeBytes);

        if (!CryptographicOperations.FixedTimeEquals(computedKey, storedKey))
            return PasswordVerifyResult.Failed;

        return iterations < _iterations
            ? PasswordVerifyResult.SuccessRehashNeeded
            : PasswordVerifyResult.Success;
    }

    /// <summary>
    /// Verifies a Duende-migrated client secret stored as a tagged unsalted digest
    /// (<c>SHA256$&lt;base64&gt;</c> / <c>SHA512$&lt;base64&gt;</c>). Duende hashed client secrets as a bare
    /// SHA-256/512 of the UTF-8 secret; the migration prepends the tag so we know which digest to
    /// recompute. Returns <see cref="PasswordVerifyResult.SuccessRehashNeeded"/> on match — but note
    /// client secrets are never rehashed on use (the secret verifier ignores the rehash signal), so
    /// this legacy format lives until the secret is rotated. Users never carry these tags.
    /// </summary>
    private static PasswordVerifyResult VerifyTaggedDigest(string password, string encodedDigest, bool sha512)
    {
        byte[] storedDigest;
        try
        {
            storedDigest = Convert.FromBase64String(encodedDigest);
        }
        catch (FormatException)
        {
            return PasswordVerifyResult.Failed;
        }

        var passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
        var computedDigest = sha512 ? SHA512.HashData(passwordBytes) : SHA256.HashData(passwordBytes);

        return CryptographicOperations.FixedTimeEquals(computedDigest, storedDigest)
            ? PasswordVerifyResult.SuccessRehashNeeded
            : PasswordVerifyResult.Failed;
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

        // Sanity checks. iterCount previously had only a lower bound while saltLength had both, so a
        // stored blob could declare 2^31-1 iterations and turn any anonymous /connect/token call for
        // that client into hours of uncancellable, thread-pool-pinning PBKDF2. Real ASP.NET Identity
        // hashes sit far below the ceiling; anything above it is not a hash this server should be
        // spending CPU on.
        if (iterCount <= 0 || iterCount > MaxImportedIterations)
            return PasswordVerifyResult.Failed;

        if (saltLength <= 0 || saltLength > 128)
            return PasswordVerifyResult.Failed;

        if (decoded.Length < 13 + saltLength)
            return PasswordVerifyResult.Failed;

        var salt = decoded.AsSpan(13, saltLength);
        var subkeyLength = decoded.Length - 13 - saltLength;

        // Bounded for the same reason: the derived-key length multiplies PBKDF2's cost, so an
        // oversized blob is a second lever on the same CPU-exhaustion primitive.
        if (subkeyLength <= 0 || subkeyLength > MaxImportedSubkeyLength)
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
