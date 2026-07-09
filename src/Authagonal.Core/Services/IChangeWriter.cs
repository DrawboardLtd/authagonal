namespace Authagonal.Core.Services;

/// <summary>Kind of change captured in the change-log so incremental backups can materialize it.</summary>
public enum ChangeOp
{
    /// <summary>Row created or updated. The backup re-reads the live row for its current state.</summary>
    Upsert,
    /// <summary>Row deleted. The backup records the key so restore removes it (and never resurrects it).</summary>
    Delete,
}

/// <summary>
/// Records the key of every changed row to a dedicated change-log table so incremental backups can find
/// what changed without scanning the (unindexed) <c>Timestamp</c> column of the live data tables.
/// <para>
/// Deletes are captured for every table (a live-row scan cannot see a row that is gone); upserts are
/// captured only for the tables the backup reads from the log rather than scanning. The delete methods keep
/// their original signatures so existing call sites are unchanged; a missed delete resurrects a row on
/// restore, so those stay on the awaited path.
/// </para>
/// </summary>
public interface IChangeWriter
{
    /// <summary>Record a delete (<see cref="ChangeOp.Delete"/>).</summary>
    Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default);

    /// <summary>Batch-record deletes that share a logical table (one Table Storage partition per call).</summary>
    Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    /// <summary>Record an upsert (<see cref="ChangeOp.Upsert"/>).</summary>
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default);

    /// <summary>Batch-record upserts that share a logical table (one Table Storage partition per call).</summary>
    Task WriteUpsertBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
