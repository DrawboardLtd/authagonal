using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// <see cref="IRateLimitCounterStore"/> over DynamoDB's native atomic <c>ADD</c>.
/// </summary>
/// <remarks>
/// The one backend where the increment needs no conditional write and no retry: DynamoDB evaluates
/// <c>ADD</c> server-side. Bucket rows expire through the table's TTL attribute, so nothing sweeps them.
/// </remarks>
public sealed class DynamoRateLimitCounterStore(DynamoTable table, EnvPartitioner partitioner) : IRateLimitCounterStore
{
    private const string CounterSk = "counter";
    private const string CounterAttribute = "n";

    public Task<long> IncrementAsync(string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default)
        => table.IncrementAsync(partitioner.PK(bucketKey), CounterSk, CounterAttribute, expiresAt, ct);
}
