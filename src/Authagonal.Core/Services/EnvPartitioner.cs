namespace Authagonal.Core.Services;

/// <summary>
/// Env-aware Azure Table PartitionKey transformer. In the <c>live</c> env, the
/// natural key (userId, normalized email, client_id, etc.) is used unchanged —
/// production tables are bit-exact identical to the single-env model. In any
/// other env, the partition key is prefixed with <c>{env}|</c> so multiple
/// sandbox envs can share one set of <c>{slug}-sandbox-*</c> tables without
/// data ever leaking across envs.
///
/// Every read and write that builds a PartitionKey from a natural key MUST go
/// through <see cref="PK"/>. A query that constructs a PK directly from user
/// input bypasses env isolation.
///
/// Lives in Authagonal.Core so the storage layer can take it as a constructor
/// dependency without referencing the Cloud layer.
/// </summary>
public sealed class EnvPartitioner
{
    public string Env { get; }
    public bool IsLive { get; }

    public EnvPartitioner(string env)
    {
        Env = env;
        IsLive = string.Equals(env, ITenantContext.LiveEnv, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Live: returns <paramref name="natural"/> unchanged. Sandbox env: returns <c>{env}|{natural}</c>.</summary>
    public string PK(string natural) => IsLive ? natural : $"{Env}|{natural}";

    /// <summary>
    /// Strips the <c>{env}|</c> prefix from a PartitionKey. Used when echoing a
    /// PK back to a caller (e.g. as a userId field on a response). On live this
    /// is a no-op.
    /// </summary>
    public string Strip(string partitionKey)
    {
        if (IsLive) return partitionKey;
        var prefix = $"{Env}|";
        return partitionKey.StartsWith(prefix, System.StringComparison.Ordinal)
            ? partitionKey[prefix.Length..]
            : partitionKey;
    }

    /// <summary>
    /// PartitionKey range filter for "all rows in this env" — used by sweep
    /// operations like wipe-on-disable. Live returns null (no range filter
    /// needed; the live tables only contain live data). Sandbox env returns the
    /// half-open range [lo, hi), consumed as <c>pk &gt;= lo &amp;&amp; pk &lt; hi</c>.
    /// </summary>
    /// <remarks>
    /// The upper bound is the exclusive SUCCESSOR of the prefix, not a sentinel inside it.
    ///
    /// It used to be <c>{env}|~</c>, on the reasoning that tilde (0x7E) sorts after any printable
    /// ASCII. Keys are not ASCII. Any natural key whose first character sorts at or above '~' fell
    /// outside the range and was invisible to every env-scoped query — internationalized email
    /// addresses and domains, non-Latin names in the legacy name index, IdP-supplied external IDs,
    /// SAML NameIDs. On PostgreSQL the same bound is used under COLLATE "C", where a UTF-8 lead byte
    /// (0xC2 and up) likewise sorts above 0x7E, so the truncation was identical on both backends.
    /// That silently under-selects a WIPE as readily as a read.
    ///
    /// Incrementing the delimiter instead makes the bound exact for every possible suffix: '|' is
    /// 0x7C, so the successor is '}' (0x7D), and no string beginning <c>{env}|</c> can reach it.
    /// </remarks>
    public (string Low, string High)? RangeForEnv()
    {
        if (IsLive) return null;
        var lo = $"{Env}|";
        var hi = Env + (char)('|' + 1);
        return (lo, hi);
    }

    /// <summary>Singleton for the live env (used in tests and single-env contexts).</summary>
    public static readonly EnvPartitioner Live = new(ITenantContext.LiveEnv);
}
