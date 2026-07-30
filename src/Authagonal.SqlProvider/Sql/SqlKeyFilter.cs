namespace Authagonal.SqlProvider.Sql;

/// <summary>
/// A typed predicate over the key columns plus (equality only) the promoted attributes — the SQL
/// counterpart of a DynamoDB key-condition/filter pair, but closed rather than string-built, so no
/// caller can compose a predicate out of untrusted text.
/// <para>
/// A filter with <see cref="Pk"/> set is a single-partition query (index seek); one with a pk range
/// or prefix is a bounded scan; one with neither is a full scan, used only by the same low-frequency
/// admin listings that scan on the Azure and AWS backends.
/// </para>
/// </summary>
public sealed record SqlKeyFilter
{
    /// <summary>Exact partition.</summary>
    public string? Pk { get; init; }

    /// <summary>Partition prefix — compiled to the half-open range [prefix, prefix⁺) so it stays a seek.</summary>
    public string? PkPrefix { get; init; }

    /// <summary>Inclusive lower bound on pk.</summary>
    public string? PkFrom { get; init; }

    /// <summary>Exclusive upper bound on pk.</summary>
    public string? PkUntil { get; init; }

    /// <summary>Exact sort key.</summary>
    public string? Sk { get; init; }

    /// <summary>Sort-key prefix (the <c>begins_with</c> equivalent).</summary>
    public string? SkPrefix { get; init; }

    /// <summary>Exclusive lower bound on sk — the cursor form, <c>sk &gt; value</c>.</summary>
    public string? SkAfter { get; init; }

    /// <summary>Inclusive upper bound on sk, <c>sk &lt;= value</c>.</summary>
    public string? SkAtMost { get; init; }

    /// <summary>Exclusive upper bound on sk, <c>sk &lt; value</c>.</summary>
    public string? SkBefore { get; init; }

    /// <summary>Equality predicates on promoted attributes, ANDed together.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? AttrEquals { get; init; }

    /// <summary>
    /// When false the <c>data</c> column is left out of the SELECT — the projection the
    /// whole-population sweeps use so they never read (and never decrypt) a document.
    /// </summary>
    public bool IncludeData { get; init; } = true;

    /// <summary>Every row in one partition.</summary>
    public static SqlKeyFilter Partition(string pk) => new() { Pk = pk };

    /// <summary>Every row whose sort key is <paramref name="sk"/>, across all partitions.</summary>
    public static SqlKeyFilter SortKey(string sk) => new() { Sk = sk };

    /// <summary>This filter plus an attribute-equality predicate.</summary>
    public SqlKeyFilter WithAttr(string name, string value) => this with
    {
        AttrEquals = [.. AttrEquals ?? [], new KeyValuePair<string, string>(name, value)],
    };
}
