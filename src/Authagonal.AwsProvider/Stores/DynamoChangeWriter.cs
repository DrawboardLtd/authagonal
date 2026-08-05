using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// Records row-level changes to a dedicated change-log table so incremental backups can enumerate what
/// changed without a scan. Each row: pk = logical table name (e.g. "Users"), sk = "{originalPk}|{originalSk}",
/// op = "U"/"D", plus authoritative <c>origPk</c>/<c>origSk</c> columns. DynamoDB has no 100-item
/// transaction, so the batch path just issues individual puts (this is a change side-channel, not a hot
/// path). Deletes keep <c>deletedAt</c> so the backup's tombstone file format is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <c>origPk</c>/<c>origSk</c> carry what <c>TableChangeWriter</c> writes as <c>OrigPK</c>/<c>OrigRK</c>,
/// in this backend's casing. They were missing, and the sort key alone is not recoverable: it is
/// <c>"{pk}|{sk}"</c>, and a '|' inside the original partition key (the sandbox <c>{env}|</c> prefix, a
/// legacy <c>{clientId}|{externalId}</c> or <c>{provider}|{providerKey}</c> key) makes the split
/// ambiguous — which is the key corruption the Azure columns exist to prevent.
/// </para>
/// <para>
/// It "mirrors TableChangeWriter" in row format only. No backup engine reads these rows on DynamoDB:
/// <c>BackupService</c> and <c>RestoreService</c> take a <c>TableServiceClient</c> and there is no
/// DynamoDB variant, so an incremental backup cannot be taken against this backend today. And upsert
/// coverage is per store: only the user store recorded them, so an incremental window carried the
/// deletions and none of the writes for agent profiles, provisioning apps and role mappings. All four
/// of Azure's stores now have a DynamoDB counterpart that records both halves.
/// </para>
/// </remarks>
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
        // Authoritative keys — see the remarks on the class ('|' in the pk makes splitting the sk ambiguous).
        item.PutS("origPk", partitionKey);
        item.PutS("origSk", rowKey);
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
            item.PutS("origPk", pk);
            item.PutS("origSk", rk);
            if (op == DeleteOp) item.PutDate("deletedAt", now);
            await table.PutAsync(item, ct).ConfigureAwait(false);
        }
    }
}
