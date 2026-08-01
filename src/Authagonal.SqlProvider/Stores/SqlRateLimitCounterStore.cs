using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// <see cref="IRateLimitCounterStore"/> over the generic SQL table, using the single-statement
/// <c>INSERT … ON CONFLICT … RETURNING</c> increment.
/// </summary>
/// <remarks>
/// Rows carry their expiry, so <see cref="SqlExpiryReaper"/> collects them with everything else — a
/// rate-limit bucket is short-lived by construction and there is nothing to reclaim once its window has
/// passed.
/// </remarks>
public sealed class SqlRateLimitCounterStore(SqlTable table, EnvPartitioner partitioner) : IRateLimitCounterStore
{
    /// <summary>Every bucket shares one sort key; the bucket identity is the partition key.</summary>
    private const string CounterSk = "counter";

    public Task<long> IncrementAsync(string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default)
        => table.IncrementAsync(partitioner.PK(bucketKey), CounterSk, expiresAt, ct);
}
