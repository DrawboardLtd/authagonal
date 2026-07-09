using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// Records row-level changes to a dedicated change-log table so incremental backups can enumerate what
/// changed without a scan. Each row: pk = logical table name (e.g. "Users"), sk = "{originalPk}|{originalSk}",
/// op = "U"/"D". Mirrors <c>TableChangeWriter</c>; DynamoDB has no 100-item transaction, so the batch path
/// just issues individual puts (this is a change side-channel, not a hot path). Deletes keep <c>deletedAt</c>
/// so the backup's tombstone file format is unchanged.
/// </summary>
public sealed class DynamoChangeWriter(DynamoTable table) : IChangeWriter
{
    private const string DeleteOp = "D";
    private const string UpsertOp = "U";

    public Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        => WriteOneAsync(tableName, partitionKey, rowKey, DeleteOp, ct);

    public Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        => WriteOneAsync(tableName, partitionKey, rowKey, UpsertOp, ct);

    private Task WriteOneAsync(string tableName, string partitionKey, string rowKey, string op, CancellationToken ct)
    {
        var item = Dyn.Item(tableName, $"{partitionKey}|{rowKey}");
        item.PutS("op", op);
        if (op == DeleteOp) item.PutDate("deletedAt", DateTimeOffset.UtcNow);
        return table.PutAsync(item, ct);
    }

    public Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
        => WriteBatchInternalAsync(tableName, keys, DeleteOp, ct);

    public Task WriteUpsertBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
        => WriteBatchInternalAsync(tableName, keys, UpsertOp, ct);

    private async Task WriteBatchInternalAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, string op, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (pk, rk) in keys)
        {
            var item = Dyn.Item(tableName, $"{pk}|{rk}");
            item.PutS("op", op);
            if (op == DeleteOp) item.PutDate("deletedAt", now);
            await table.PutAsync(item, ct).ConfigureAwait(false);
        }
    }
}
