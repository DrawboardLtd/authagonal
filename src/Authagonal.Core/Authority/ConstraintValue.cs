namespace Authagonal.Core.Authority;

/// <summary>
/// One constraint value on an <see cref="AuthorityGrant"/> — the typed form of a custom
/// (non-standard) member of an RFC 9396 authorization-details object. Three interpretable
/// shapes plus two sentinels:
/// <list type="bullet">
/// <item><see cref="StringSet"/> — an allowlist (recipient domains, calendar ids, hidden fields).
/// Meet = set intersection.</item>
/// <item><see cref="Number"/> — a numeric cap (booking window, spend cap, rate). Meet = min.</item>
/// <item><see cref="Flag"/> — a boolean gate. Meet = AND.</item>
/// <item><see cref="Opaque"/> — a member the library cannot interpret (nested object, mixed
/// array). Preserved verbatim on the wire; meets only with a byte-identical peer, otherwise
/// collapses to <see cref="Nothing"/>.</item>
/// <item><see cref="Nothing"/> — the bottom value: no context satisfies it. Produced by a
/// kind-mismatched or conflicting meet, so a mis-configured pair of grants fails closed
/// instead of silently widening.</item>
/// </list>
/// </summary>
public abstract record ConstraintValue
{
    private ConstraintValue() { }

    public sealed record StringSet(IReadOnlyList<string> Values) : ConstraintValue;

    public sealed record Number(decimal Value) : ConstraintValue;

    public sealed record Flag(bool Value) : ConstraintValue;

    /// <summary>Uninterpreted raw JSON, preserved for round-tripping. <paramref name="RawJson"/>
    /// is the member's exact serialized value.</summary>
    public sealed record Opaque(string RawJson) : ConstraintValue;

    public sealed record NothingValue : ConstraintValue
    {
        internal NothingValue() { }
    }

    /// <summary>The bottom constraint: satisfied by no context. See the type docs.</summary>
    public static readonly ConstraintValue Nothing = new NothingValue();

    public static ConstraintValue Of(params string[] values) => new StringSet(values);
    public static ConstraintValue Of(decimal value) => new Number(value);
    public static ConstraintValue Of(bool value) => new Flag(value);

    /// <summary>
    /// The default meet (greatest lower bound): the result never permits a context either
    /// input would have refused. Same-kind pairs merge by shape (∩ / min / AND); anything
    /// else — including any pair involving <see cref="Nothing"/> or non-identical
    /// <see cref="Opaque"/> values — collapses to <see cref="Nothing"/>.
    /// </summary>
    public static ConstraintValue Meet(ConstraintValue a, ConstraintValue b) => (a, b) switch
    {
        (StringSet sa, StringSet sb) => new StringSet(
            sa.Values.Intersect(sb.Values, StringComparer.Ordinal).ToList()),
        (Number na, Number nb) => new Number(Math.Min(na.Value, nb.Value)),
        (Flag fa, Flag fb) => new Flag(fa.Value && fb.Value),
        (Opaque oa, Opaque ob) when string.Equals(oa.RawJson, ob.RawJson, StringComparison.Ordinal) => oa,
        _ => Nothing,
    };
}

/// <summary>
/// Host override for constraint meet semantics, keyed by constraint name. Return null to fall
/// back to <see cref="ConstraintValue.Meet"/>. An override must never widen: the returned value
/// must not be satisfiable by any context that either input refuses — the algebra's never-widen
/// property is only as strong as the mergers plugged into it.
/// </summary>
public interface IConstraintMerger
{
    ConstraintValue? Merge(string constraintName, ConstraintValue a, ConstraintValue b);
}
