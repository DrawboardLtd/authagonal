using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// Writes tombstone records to a dedicated table so incremental backups can capture deletes.
/// Each row: pk = logical table name (e.g. "Users"), sk = "{originalPk}|{originalSk}", deletedAt = now.
/// Mirrors <c>TableTombstoneWriter</c>; DynamoDB has no 100-item transaction, so the batch path just
/// issues individual puts (this is a delete-tracking side-channel, not a hot path).
/// </summary>
public sealed class DynamoTombstoneWriter(DynamoTable table) : ITombstoneWriter
{
    public Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        var item = Dyn.Item(tableName, $"{partitionKey}|{rowKey}");
        item.PutDate("deletedAt", DateTimeOffset.UtcNow);
        return table.PutAsync(item, ct);
    }

    public async Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (pk, rk) in keys)
        {
            var item = Dyn.Item(tableName, $"{pk}|{rk}");
            item.PutDate("deletedAt", now);
            await table.PutAsync(item, ct).ConfigureAwait(false);
        }
    }
}
