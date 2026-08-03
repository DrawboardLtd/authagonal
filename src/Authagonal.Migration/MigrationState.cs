using Azure;
using Azure.Data.Tables;

namespace Authagonal.Migration;

/// <summary>Run-once marker for the Duende migration. Single partition (<c>"duende"</c>), RowKey = Version.</summary>
public sealed class MigrationStateEntity : ITableEntity
{
    public const string PartitionKeyValue = "duende";

    public const string StatusStarted = "Started";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";

    public string PartitionKey { get; set; } = PartitionKeyValue;
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Status { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? NodeId { get; set; }
    public bool DryRun { get; set; }
    public string? StatsJson { get; set; }
    public string? Error { get; set; }

    /// <summary>Only a completed real (non-dry) run blocks a re-run of the same version.</summary>
    /// <summary>Status recorded when passes ran but at least one reported errors.</summary>
    /// <remarks>
    /// Distinct from <see cref="StatusFailed"/> (the run itself threw) and from
    /// <see cref="StatusCompleted"/>, and deliberately does NOT block a re-run: every pass is documented as
    /// idempotent report-and-skip, so retrying a partially-failed migration is safe and is the only way the
    /// missing rows ever arrive.
    /// </remarks>
    public const string StatusCompletedWithErrors = "CompletedWithErrors";

    /// <summary>
    /// Whether this record stops the hosted runner from attempting the migration again.
    /// </summary>
    /// <remarks>
    /// Only a CLEAN completion blocks. The runner used to write <see cref="StatusCompleted"/> on any return
    /// from the engine, and the engine deliberately swallows every pass exception into <c>report.Errors</c>
    /// so one failure does not abort the copy — so a run whose Users pass threw was indistinguishable from a
    /// clean one and would never be retried. The CLI got this right
    /// (<c>return report.Errors.Count == 0 ? 0 : 2</c>); the hosted runner did not.
    /// </remarks>
    public bool BlocksRerun => Status == StatusCompleted && !DryRun;
}

/// <summary>Table-backed accessor for the <see cref="MigrationStateEntity"/> marker.</summary>
public sealed class MigrationStateStore(TableClient table)
{
    /// <summary>The marker for <paramref name="version"/>, or null if the migration hasn't run for it.</summary>
    public async Task<MigrationStateEntity?> GetAsync(string version, CancellationToken ct = default)
    {
        try
        {
            var response = await table.GetEntityAsync<MigrationStateEntity>(
                MigrationStateEntity.PartitionKeyValue, version, cancellationToken: ct);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task UpsertAsync(MigrationStateEntity entity, CancellationToken ct = default)
        => table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
}
