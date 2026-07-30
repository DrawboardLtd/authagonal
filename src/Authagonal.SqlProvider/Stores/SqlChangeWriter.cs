using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// Records row-level changes to a dedicated change-log table so incremental backups can enumerate
/// what changed without a scan. Each row: pk = logical table name (e.g. "Users"),
/// sk = "{originalPk}|{originalSk}", op = "U"/"D". Mirrors <c>TableChangeWriter</c> and
/// <c>DynamoChangeWriter</c> exactly, so a backup taken against SQL restores onto any backend.
/// Deletes keep <c>deletedAt</c> so the tombstone file format is unchanged.
/// </summary>
public sealed class SqlChangeWriter(SqlTable table) : IChangeWriter
{
    private const string DeleteOp = "D";
    private const string UpsertOp = "U";

    public Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        => WriteOneAsync(tableName, partitionKey, rowKey, DeleteOp, DateTimeOffset.UtcNow, ct);

    public Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        => WriteOneAsync(tableName, partitionKey, rowKey, UpsertOp, DateTimeOffset.UtcNow, ct);

    public Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
        => WriteBatchInternalAsync(tableName, keys, DeleteOp, ct);

    public Task WriteUpsertBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
        => WriteBatchInternalAsync(tableName, keys, UpsertOp, ct);

    private Task WriteOneAsync(string tableName, string partitionKey, string rowKey, string op, DateTimeOffset now, CancellationToken ct)
    {
        var row = new SqlRow(tableName, $"{partitionKey}|{rowKey}");
        row.PutS("op", op);
        if (op == DeleteOp) row.PutDate("deletedAt", now);
        return table.PutAsync(row, ct);
    }

    private async Task WriteBatchInternalAsync(
        string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, string op, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (pk, rk) in keys)
            await WriteOneAsync(tableName, pk, rk, op, now, ct).ConfigureAwait(false);
    }
}
