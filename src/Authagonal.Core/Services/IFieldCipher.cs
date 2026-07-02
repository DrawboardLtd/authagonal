namespace Authagonal.Core.Services;

/// <summary>
/// Encrypts individual user PII field values at rest. Implementations wrap a per-tenant
/// key (in Cloud, the Vault Transit <c>enc-{prefix}</c> key each tenant already gets at
/// provisioning). The default <see cref="NullFieldCipher"/> is a passthrough, so
/// single-tenant / unconfigured hosts store plaintext and encryption is strictly opt-in.
///
/// Contract:
/// <list type="bullet">
/// <item><see cref="ProtectAsync"/> returns a self-describing ciphertext token (e.g. Vault's
/// <c>vault:v{n}:...</c>) that <see cref="ResolveAsync"/> reverses.</item>
/// <item><see cref="ResolveAsync"/> MUST pass a value it does not recognise as its own
/// ciphertext (i.e. legacy plaintext written before encryption was enabled) through
/// unchanged. This is what lets encryption roll out lazily over existing rows — a read of
/// an un-migrated row returns plaintext, and the next write re-protects it.</item>
/// </list>
/// Callers pass only non-null, non-empty values; null/empty handling stays with the caller.
/// </summary>
public interface IFieldCipher
{
    /// <summary>Encrypt <paramref name="plaintext"/>, returning a token <see cref="ResolveAsync"/> can reverse.</summary>
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);

    /// <summary>Decrypt a token from <see cref="ProtectAsync"/>; return unrecognised (legacy plaintext) input unchanged.</summary>
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);
}

/// <summary>
/// Passthrough <see cref="IFieldCipher"/> — stores and returns values unchanged. The implicit
/// default when no tenant field encryption is configured, so existing deployments are unaffected.
/// </summary>
public sealed class NullFieldCipher : IFieldCipher
{
    public static readonly NullFieldCipher Instance = new();

    public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default) => Task.FromResult(plaintext);
    public Task<string> ResolveAsync(string stored, CancellationToken ct = default) => Task.FromResult(stored);
}
