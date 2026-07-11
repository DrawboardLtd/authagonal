using Azure.Data.Tables;
using Authagonal.Core.Services;

namespace Authagonal.AzureProvider.Stores;

/// <summary>
/// Records row-level changes to a dedicated change-log table (physically still named <c>Tombstones</c>) so
/// incremental backups can enumerate what changed without a <c>Timestamp</c> scan of the live data tables.
/// Each row: PK = logical table name (e.g. "Users"), RK = "{originalPK}|{originalRK}", <c>Op</c> = "U"/"D",
/// plus authoritative <c>OrigPK</c>/<c>OrigRK</c> columns. The composite RK stays as the upsert-replace dedup
/// key, but the backup reader recovers the original key from the columns — a '|' in the PK (sandbox
/// <c>{env}|</c> prefix, legacy <c>{clientId}|{externalId}</c> / <c>{provider}|{providerKey}</c>) makes
/// splitting the RK ambiguous.
/// A given key holds one row (upsert-replace), so the last op in a backup window wins — an upsert then a
/// delete of the same key resolves to a delete, a delete then a re-create resolves to an upsert. Deletes
/// keep the historical <c>DeletedAt</c> column so the backup's <c>_tombstones</c> file format is unchanged.
/// </summary>
public sealed class TableChangeWriter(TableClient changeLogTable) : IChangeWriter
{
    private const string DeleteOp = "D";
    private const string UpsertOp = "U";

    public Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        => WriteOneAsync(tableName, partitionKey, rowKey, DeleteOp, ct);

    public Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        => WriteOneAsync(tableName, partitionKey, rowKey, UpsertOp, ct);

    private Task WriteOneAsync(string tableName, string partitionKey, string rowKey, string op, CancellationToken ct)
    {
        var entity = new TableEntity(tableName, $"{partitionKey}|{rowKey}")
        {
            { "Op", op },
            { "OrigPK", partitionKey }, // authoritative key columns — see the class summary (RK '|' is ambiguous)
            { "OrigRK", rowKey },
        };
        // DeletedAt preserved for the backup's tombstone file (Table (from PK), PK/RK (from OrigPK/OrigRK), DeletedAt).
        if (op == DeleteOp) entity["DeletedAt"] = DateTimeOffset.UtcNow;
        return changeLogTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
        => WriteBatchInternalAsync(tableName, keys, DeleteOp, ct);

    public Task WriteUpsertBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
        => WriteBatchInternalAsync(tableName, keys, UpsertOp, ct);

    private async Task WriteBatchInternalAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, string op, CancellationToken ct)
    {
        // All rows share PartitionKey = tableName, so a single entity-group transaction is valid.
        var batch = new List<TableTransactionAction>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (pk, rk) in keys)
        {
            var entity = new TableEntity(tableName, $"{pk}|{rk}")
            {
                { "Op", op },
                { "OrigPK", pk },
                { "OrigRK", rk },
            };
            if (op == DeleteOp) entity["DeletedAt"] = now;
            batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, entity));

            // Azure Table Storage limit: 100 entities per transaction, same partition key
            if (batch.Count >= 100)
            {
                await changeLogTable.SubmitTransactionAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await changeLogTable.SubmitTransactionAsync(batch, ct);
    }
}
