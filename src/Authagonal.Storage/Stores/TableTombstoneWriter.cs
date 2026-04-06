using Azure.Data.Tables;
using Authagonal.Core.Services;

namespace Authagonal.Storage.Stores;

/// <summary>
/// Writes tombstone records to a dedicated Tombstones table for backup-aware delete tracking.
/// Each row: PK = logical table name (e.g. "Users"), RK = "{originalPK}|{originalRK}", DeletedAt = timestamp.
/// </summary>
public sealed class TableTombstoneWriter(TableClient tombstonesTable) : ITombstoneWriter
{
    public async Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        var entity = new TableEntity(tableName, $"{partitionKey}|{rowKey}")
        {
            { "DeletedAt", DateTimeOffset.UtcNow }
        };
        await tombstonesTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
    {
        var batch = new List<TableTransactionAction>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (pk, rk) in keys)
        {
            var entity = new TableEntity(tableName, $"{pk}|{rk}")
            {
                { "DeletedAt", now }
            };
            batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, entity));

            // Azure Table Storage limit: 100 entities per transaction, same partition key
            if (batch.Count >= 100)
            {
                await tombstonesTable.SubmitTransactionAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await tombstonesTable.SubmitTransactionAsync(batch, ct);
    }
}
