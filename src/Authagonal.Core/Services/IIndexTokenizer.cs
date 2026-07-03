namespace Authagonal.Core.Services;

/// <summary>
/// Turns a normalized plaintext value into a deterministic, table-key-safe <b>blind-index token</b> — a
/// keyed HMAC (in Cloud, Vault Transit HMAC under the per-tenant <c>idx-{prefix}</c> key). Because it is
/// deterministic, equality lookups still work over encrypted data — "email = x" becomes "token = HMAC(x)";
/// because the key lives in Vault and never in the database, a dump can neither recompute a token from a
/// value nor reverse one. The default <see cref="NullIndexTokenizer"/> passes values through unchanged, so
/// index rows stay keyed on plaintext (current behavior) and tokenization is strictly opt-in.
///
/// Prefix search is layered on top (inc 4) by tokenizing each prefix of a normalized value separately, so
/// "starts with p" becomes an equality lookup "token = HMAC(p)" against a per-prefix index row — a keyed
/// HMAC destroys ordering, so an ordered range scan is impossible; per-prefix tokens are how prefix
/// matching survives.
///
/// Contract: callers pass only non-null, non-empty values. The returned token is safe to use directly as
/// an Azure Table PartitionKey / RowKey (contains none of <c>/ \ # ?</c> or control chars).
/// </summary>
public interface IIndexTokenizer
{
    /// <summary>Tokenize one value into its blind-index token.</summary>
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);

    /// <summary>Batched <see cref="TokenizeAsync"/> — one round-trip for many values (a name's prefix set,
    /// a bulk reindex). Returns tokens in the SAME order as <paramref name="values"/>.</summary>
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values, CancellationToken ct = default);
}

/// <summary>
/// Passthrough <see cref="IIndexTokenizer"/> — returns values unchanged, so index rows stay keyed on
/// plaintext. The implicit default when blind indexing is not configured, keeping every existing
/// deployment on its current (plaintext-keyed) index path.
/// </summary>
public sealed class NullIndexTokenizer : IIndexTokenizer
{
    public static readonly NullIndexTokenizer Instance = new();

    public Task<string> TokenizeAsync(string value, CancellationToken ct = default) => Task.FromResult(value);

    public Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values, CancellationToken ct = default)
        => Task.FromResult(values);
}
