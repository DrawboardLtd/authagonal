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

    /// <summary>Encrypt many values in one shot, results in input order. The default loops
    /// <see cref="ProtectAsync"/>; a backend with a batch primitive (Vault Transit) overrides this to do it
    /// in a single round-trip. Callers pass only non-empty values.</summary>
    async Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts, CancellationToken ct = default)
    {
        var r = new string[plaintexts.Count];
        for (var i = 0; i < plaintexts.Count; i++) r[i] = await ProtectAsync(plaintexts[i], ct);
        return r;
    }

    /// <summary>Resolve many values in one shot, results in input order (legacy plaintext passed through
    /// per item, as in <see cref="ResolveAsync"/>). The default loops; a batch backend overrides to
    /// decrypt the ciphertext items in a single round-trip. Callers pass only non-empty values.</summary>
    async Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored, CancellationToken ct = default)
    {
        var r = new string[stored.Count];
        for (var i = 0; i < stored.Count; i++) r[i] = await ResolveAsync(stored[i], ct);
        return r;
    }
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
