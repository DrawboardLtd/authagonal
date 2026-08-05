using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// Records row-level changes to a dedicated change-log table so incremental backups can enumerate
/// what changed without a scan. Each row: pk = logical table name (e.g. "Users"),
/// sk = "{originalPk}|{originalSk}", op = "U"/"D", plus authoritative <c>origPk</c>/<c>origSk</c>
/// columns. Deletes keep <c>deletedAt</c> so the tombstone file format is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The <c>origPk</c>/<c>origSk</c> columns carry the same information as <c>TableChangeWriter</c>'s
/// <c>OrigPK</c>/<c>OrigRK</c>, in this backend's own casing (as <c>op</c> and <c>deletedAt</c> already
/// are). They were absent here, and that is not cosmetic: the composite sort key is
/// <c>"{pk}|{sk}"</c>, and a '|' inside the original partition key — the sandbox <c>{env}|</c> prefix,
/// or a legacy <c>{clientId}|{externalId}</c> / <c>{provider}|{providerKey}</c> key — makes splitting
/// it ambiguous. Without the columns every row is a row a reader has to guess at, so a SCIM-provisioned
/// user keyed <c>"{clientId}|{externalId}"</c> would restore under partition key <c>{clientId}</c>.
/// That is precisely the corruption the Azure writer's columns were added to prevent.
/// </para>
/// <para>
/// This summary previously claimed the writer "mirrors <c>TableChangeWriter</c> and
/// <c>DynamoChangeWriter</c> exactly, so a backup taken against SQL restores onto any backend". The
/// row format now does match. Two things it still does NOT claim, because they are not true:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>No backup engine reads these rows on this backend.</b> <c>BackupService</c> and
/// <c>RestoreService</c> are constructed from a <c>TableServiceClient</c>, and there is no SQL variant,
/// so an incremental backup cannot be taken against SQL at all today. These writes are a side-channel
/// kept correct for the engine that would read them, not evidence that one exists. An operator planning
/// disaster recovery around incremental backups of a Postgres identity store should read
/// <c>docs/backup-restore.md</c>'s Azure scope first.
/// </item>
/// <item>
/// <b>Upsert coverage is per store, not automatic.</b> A store records upserts only if it calls
/// <see cref="WriteUpsertAsync"/>, and only the user store did — so an incremental window carried the
/// deletions and none of the writes for agent profiles, provisioning apps and role mappings. All four
/// of Azure's stores now have a SQL counterpart that records both halves.
/// </item>
/// </list>
/// </remarks>
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
        // Authoritative keys — see the remarks on the class ('|' in the pk makes splitting the sk ambiguous).
        row.PutS("origPk", partitionKey);
        row.PutS("origSk", rowKey);
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
