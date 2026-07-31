using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Authagonal.Backup;

/// <summary>
/// Computes and checks the manifest's HMAC, so the file hashes it carries authenticate the backup
/// rather than merely describing it.
/// </summary>
/// <remarks>
/// The key must come from somewhere the backup writer cannot reach — a vault entry, a KMS key, an
/// environment secret on the restoring host. Storing it next to the backup would reproduce exactly
/// the circularity this exists to remove.
/// </remarks>
public static class ManifestAuthentication
{
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false,
    };

    /// <summary>Computes the MAC over <paramref name="manifest"/> with its own MAC field cleared.</summary>
    public static string Compute(BackupManifest manifest, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(key);

        // The MAC cannot cover itself, so it is excluded from the input on both sides.
        var previous = manifest.ManifestMac;
        manifest.ManifestMac = null;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalJson);
            return Convert.ToHexString(HMACSHA256.HashData(key, payload)).ToLowerInvariant();
        }
        finally
        {
            manifest.ManifestMac = previous;
        }
    }

    /// <summary>Stamps the MAC onto the manifest.</summary>
    public static void Sign(BackupManifest manifest, byte[] key)
        => manifest.ManifestMac = Compute(manifest, key);

    /// <summary>
    /// True when the manifest carries a MAC that verifies under <paramref name="key"/>. False when it
    /// carries none, or one that does not match.
    /// </summary>
    public static bool Verify(BackupManifest manifest, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrEmpty(manifest.ManifestMac)) return false;

        var expected = Compute(manifest, key);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(manifest.ManifestMac));
    }
}
