using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Core;
using Azure.Data.Tables;

// ---------------------------------------------------------------------------
// Authagonal Table Storage Backup Tool
//
// Usage:
//   authagonal-backup --connection-string "..." --output ./backups
//   authagonal-backup --connection-string "..." --output ./backups --incremental
//   authagonal-backup --connection-string "..." --output ./backups --tables Users,Clients
//   authagonal-backup --connection-string "..." --output ./backups --gzip
//
// Environment variable:
//   STORAGE_CONNECTION_STRING — fallback when --connection-string is not given
//
// Incremental mode:
//   Backs up only entities modified since the last successful backup.
//   Uses Azure Table Storage's built-in Timestamp property for filtering.
//   A .lastbackup file in the output directory tracks the high-water mark.
//
// Output:
//   <output>/<timestamp>/           (full backup)
//   <output>/<timestamp>-incr/      (incremental backup)
//     <TableName>.jsonl             (uncompressed, default)
//     <TableName>.jsonl.gz          (gzip compressed, with --gzip)
//     _manifest.json                (metadata about the backup)
// ---------------------------------------------------------------------------

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

var connectionString = GetArg(cliArgs, "--connection-string") ?? Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
var outputRoot = GetArg(cliArgs, "--output") ?? "./backups";
var incremental = HasFlag(cliArgs, "--incremental");
var tableFilter = GetArg(cliArgs, "--tables")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var dryRun = HasFlag(cliArgs, "--dry-run");
var useGzip = HasFlag(cliArgs, "--gzip");

if (connectionString is null || HasFlag(cliArgs, "--help"))
{
    Console.WriteLine("""
    Authagonal Table Storage Backup Tool

    Usage:
      authagonal-backup --connection-string <conn> [options]

    Options:
      --connection-string <conn>   Azure Table Storage connection string
                                   (or set STORAGE_CONNECTION_STRING env var)
      --output <dir>               Output directory (default: ./backups)
      --incremental                Only back up entities changed since last backup
      --tables <t1,t2,...>         Comma-separated list of tables to back up
      --gzip                       Compress backup files with gzip (.jsonl.gz)
      --dry-run                    Show what would be backed up without writing
      --help                       Show this help

    Incremental backups use Azure Table Storage's Timestamp property to detect
    changes. A .lastbackup file in the output directory tracks the watermark.
    """);
    return connectionString is null && !HasFlag(cliArgs, "--help") ? 1 : 0;
}

// All Authagonal tables. SamlReplayCache and OidcStateStore are transient
// (10-min TTL) and excluded by default — include explicitly with --tables.
string[] defaultTables =
[
    "Users", "UserEmails", "UserLogins",
    "Clients",
    "Grants", "GrantsBySubject", "GrantsByExpiry",
    "SigningKeys",
    "SsoDomains",
    "SamlProviders", "OidcProviders",
    "UserProvisions"
];

string[] transientTables = ["SamlReplayCache", "OidcStateStore"];

var tablesToBackup = tableFilter ?? defaultTables;

var clientOptions = new TableClientOptions();
clientOptions.Retry.MaxRetries = 5;
clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(500);
clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(30);
clientOptions.Retry.Mode = RetryMode.Exponential;

var serviceClient = new TableServiceClient(connectionString, clientOptions);
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// Determine incremental watermark
DateTimeOffset? watermark = null;
var lastBackupFile = Path.Combine(outputRoot, ".lastbackup");

if (incremental && File.Exists(lastBackupFile))
{
    var text = File.ReadAllText(lastBackupFile).Trim();
    if (DateTimeOffset.TryParse(text, out var parsed))
    {
        watermark = parsed;
        Console.WriteLine($"Incremental backup from: {watermark:O}");
    }
    else
    {
        Console.WriteLine($"Warning: could not parse .lastbackup ({text}), falling back to full backup");
    }
}
else if (incremental)
{
    Console.WriteLine("No .lastbackup file found — performing full backup");
}

var backupStart = DateTimeOffset.UtcNow;
var suffix = watermark.HasValue ? "-incr" : "";
var backupDir = Path.Combine(outputRoot, backupStart.ToString("yyyyMMdd-HHmmss") + suffix);

if (!dryRun)
{
    Directory.CreateDirectory(backupDir);
}

Console.WriteLine($"Backup directory: {backupDir}");
Console.WriteLine($"Tables: {string.Join(", ", tablesToBackup)}");
Console.WriteLine($"Mode: {(watermark.HasValue ? "incremental" : "full")}{(useGzip ? ", gzip" : "")}");
if (dryRun) Console.WriteLine("DRY RUN — no files will be written");
Console.WriteLine();

var sw = Stopwatch.StartNew();
var manifest = new Dictionary<string, TableBackupInfo>();
long totalEntities = 0;

foreach (var tableName in tablesToBackup)
{
    var tableClient = serviceClient.GetTableClient(tableName);
    var tableStart = Stopwatch.StartNew();

    // Build OData filter for incremental backup
    string? filter = null;
    if (watermark.HasValue)
    {
        filter = $"Timestamp gt datetime'{watermark.Value:O}'";
    }

    long count = 0;
    StreamWriter? writer = null;
    Stream? fileStream = null;
    Stream? gzipStream = null;

    try
    {
        // Query all entities as generic TableEntity
        var pages = tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: 1000);

        await foreach (var entity in pages)
        {
            if (writer is null && !dryRun)
            {
                var ext = useGzip ? ".jsonl.gz" : ".jsonl";
                var filePath = Path.Combine(backupDir, $"{tableName}{ext}");
                fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                if (useGzip)
                {
                    gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
                    writer = new StreamWriter(gzipStream, System.Text.Encoding.UTF8);
                }
                else
                {
                    writer = new StreamWriter(fileStream, System.Text.Encoding.UTF8);
                }
            }

            if (!dryRun)
            {
                var dict = new Dictionary<string, object?>
                {
                    ["PartitionKey"] = entity.PartitionKey,
                    ["RowKey"] = entity.RowKey,
                    ["Timestamp"] = entity.Timestamp,
                    ["ETag"] = entity.ETag.ToString(),
                };

                foreach (var kvp in entity)
                {
                    if (kvp.Key is "odata.etag") continue;
                    dict.TryAdd(kvp.Key, kvp.Value);
                }

                writer!.WriteLine(JsonSerializer.Serialize(dict, jsonOptions));
            }

            count++;
        }
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
        Console.WriteLine($"  {tableName}: table not found (skipped)");
        continue;
    }
    finally
    {
        writer?.Dispose();
        gzipStream?.Dispose();
        fileStream?.Dispose();
    }

    tableStart.Stop();
    totalEntities += count;
    manifest[tableName] = new TableBackupInfo(count, tableStart.Elapsed.TotalSeconds);

    Console.WriteLine($"  {tableName}: {count:N0} entities ({tableStart.Elapsed.TotalSeconds:F1}s)");
}

sw.Stop();

// Write manifest
if (!dryRun)
{
    var manifestObj = new
    {
        BackupTimestamp = backupStart,
        Mode = watermark.HasValue ? "incremental" : "full",
        Compressed = useGzip,
        Watermark = watermark,
        Tables = manifest,
        TotalEntities = totalEntities,
        DurationSeconds = sw.Elapsed.TotalSeconds,
    };

    var manifestPath = Path.Combine(backupDir, "_manifest.json");
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = true }));

    // Update watermark only on success
    Directory.CreateDirectory(outputRoot);
    await File.WriteAllTextAsync(lastBackupFile, backupStart.ToString("O"));
}

Console.WriteLine();
Console.WriteLine($"Done: {totalEntities:N0} entities in {sw.Elapsed.TotalSeconds:F1}s");

return 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static bool HasFlag(string[] args, string name)
    => args.Contains(name);

record TableBackupInfo(long EntityCount, double DurationSeconds);
