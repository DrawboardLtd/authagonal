namespace Authagonal.Core.Services;

/// <summary>
/// Settings for the <see cref="ISecretProvider"/> seam. Bound from the "SecretProvider"
/// configuration section by <c>AddAuthagonal</c>.
/// </summary>
public sealed class SecretProviderOptions
{
    /// <summary>
    /// When true, a stored reference that carries no vault prefix (<c>kv:</c> for Key Vault,
    /// <c>sm:</c> for Secrets Manager) is an error rather than a plaintext secret value.
    /// Default false, which keeps the migration path open.
    /// </summary>
    /// <remarks>
    /// The vault-backed providers return an unprefixed reference verbatim, on the reasoning that it
    /// is a legacy plaintext value written before the deployment moved to a vault — that passthrough
    /// is what lets a running system migrate without rewriting its stored rows first. What it also
    /// is, left on forever, is a downgrade with no way to close it: anything that can write one
    /// configuration column — a partial migration, an admin path that stores a raw value where a
    /// reference belongs, an attacker with storage access and none to the vault — turns a
    /// vault-protected secret into a value of its own choosing, and the result verifies perfectly
    /// because for an unprefixed reference the reference IS the value. Set this once the migration
    /// has finished and the bypass costs an error instead of passing silently.
    /// </remarks>
    public bool RequireVaultReferences { get; set; }
}
