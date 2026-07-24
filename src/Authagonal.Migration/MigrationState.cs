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
