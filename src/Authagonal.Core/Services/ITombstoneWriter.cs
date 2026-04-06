namespace Authagonal.Core.Services;

/// <summary>
/// Writes tombstone records when entities are deleted, enabling incremental backups to capture deletes.
/// </summary>
public interface ITombstoneWriter
{
    Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default);
    Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
