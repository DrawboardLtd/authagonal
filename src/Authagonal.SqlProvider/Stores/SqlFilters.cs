using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>Filter shapes shared by the config-style stores (one config row per natural key).</summary>
internal static class SqlFilters
{
    /// <summary>
    /// Every row with sort key <paramref name="sk"/>, bounded to the current env's pk range when the
    /// partitioner is a sandbox. Backed by the <c>(sk, pk)</c> index, so it is a range seek rather
    /// than a table scan — the one place the SQL backend does materially better than a DynamoDB
    /// filtered scan, which has to examine every item.
    /// </summary>
    public static SqlKeyFilter Config(EnvPartitioner partitioner, string sk)
    {
        var range = partitioner.RangeForEnv();
        return range is null
            ? SqlKeyFilter.SortKey(sk)
            : SqlKeyFilter.SortKey(sk) with { PkFrom = range.Value.Low, PkUntil = range.Value.High };
    }

    /// <summary>The env's pk range with no sort-key constraint — for whole-table migrations.</summary>
    public static SqlKeyFilter Env(EnvPartitioner partitioner)
    {
        var range = partitioner.RangeForEnv();
        return range is null
            ? new SqlKeyFilter()
            : new SqlKeyFilter { PkFrom = range.Value.Low, PkUntil = range.Value.High };
    }
}
