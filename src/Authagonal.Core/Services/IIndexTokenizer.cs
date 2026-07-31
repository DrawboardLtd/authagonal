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
/// <remarks>
/// <b>What a dump still reveals.</b> "A dump can neither recompute a token from a value nor reverse
/// one" is true of a single token and NOT true of the index as a whole. Three residues survive, and
/// they are worth stating plainly because the sentence above will otherwise be read as stronger than
/// it is:
/// <list type="bullet">
/// <item><description>
/// <b>Structure.</b> The prefix index emits one row per prefix of a value, so the ROW COUNT for a
/// record equals the length of the indexed field. A dump therefore leaks how long every email
/// local-part and every name is, without breaking a single token.
/// </description></item>
/// <item><description>
/// <b>Equality and frequency.</b> Tokens are deterministic by construction — that is what makes
/// equality lookup work — so a dump shows which records share a value, and how common each value is.
/// The domain index in particular buckets the population by employer, which is often enough to
/// identify individuals without recovering any address.
/// </description></item>
/// <item><description>
/// <b>Chosen plaintext.</b> An attacker who can BOTH read the store and cause values to be indexed
/// (register an account, be provisioned over SCIM) can submit a candidate and look for its token.
/// That recovers any guessable value — every common domain, every common first name — regardless of
/// the key living in Vault, because the oracle is the product's own write path rather than the
/// cipher.
/// </description></item>
/// </list>
/// Tokenization defends against the case it was built for: an attacker with a dump and nothing else,
/// trying to read addresses. It is not a defence against an attacker who also holds a registration
/// oracle. A deployment that cannot accept the residue above should leave the prefix and domain
/// indexes off (search degrades to exact-match lookup, which carries none of it) rather than assume
/// the HMAC covers them.
/// </remarks>
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
