namespace Authagonal.Backup;

/// <summary>
/// Abstraction for writing backup data (filesystem, blob storage, etc.).
/// </summary>
public interface IBackupTarget
{
    /// <summary>Opens a writable stream for a backup file (e.g. "Users.jsonl.gz").</summary>
    Task<Stream> OpenWriteAsync(string backupId, string fileName, CancellationToken ct = default);

    /// <summary>Writes the backup manifest.</summary>
    Task WriteManifestAsync(string backupId, BackupManifest manifest, CancellationToken ct = default);

    /// <summary>Gets the last successful backup watermark (for incremental backups).</summary>
    /// <param name="scope">
    /// Which run's watermark. Null is the unscoped legacy value, so an existing target keeps working.
    /// </param>
    /// <remarks>
    /// Scoped because it was one value per output directory, advanced by ANY successful run — and both of the
    /// ways that breaks come from documented option combinations.
    /// <para>
    /// Multi-tenant, which is the stated purpose of <c>--prefix</c>: hourly
    /// <c>--prefix tenantA --incremental --output /backups</c> has been running for a month, so the stored
    /// watermark is recent. Tenant B is onboarded and its FIRST EVER run is
    /// <c>--prefix tenantB --incremental --output /backups</c>. The value exists, so the full-backup fallback
    /// does not fire, and tenant B's archive contains only rows changed in the last hour — a first backup that
    /// silently covers almost nothing.
    /// </para>
    /// <para>
    /// And a subset run: <c>--tables Users,Clients</c> advanced the watermark for every table, so the next
    /// full-set incremental skipped everything committed to the other tables before it.
    /// </para>
    /// </remarks>
    Task<DateTimeOffset?> GetLastWatermarkAsync(CancellationToken ct = default, string? scope = null);

    /// <summary>Updates the watermark after a successful backup.</summary>
    /// <param name="scope"><inheritdoc cref="GetLastWatermarkAsync" path="/param[@name='scope']"/></param>
    Task SetLastWatermarkAsync(
        DateTimeOffset watermark, CancellationToken ct = default, string? scope = null);
}
