using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;

// ---------------------------------------------------------------------------
// Authagonal Table Storage Restore Tool
//
// Usage:
//   authagonal-restore --connection-string "..." --input ./backups/20260329-120000
//   authagonal-restore --connection-string "..." --input ./backups/20260329-120000 --tables Users,Clients
//   authagonal-restore --connection-string "..." --input ./backups/20260329-120000 --dry-run
//   authagonal-restore --connection-string "..." --input ./backups/20260329-120000 --mode upsert
//
// Environment variable:
//   STORAGE_CONNECTION_STRING — fallback when --connection-string is not given
//
// Modes:
//   upsert  (default) — Insert or replace each entity. Existing data is overwritten.
//   merge   — Insert or merge. Existing properties not in the backup are preserved.
//   clean   — Delete all existing data in each table before restoring.
//
// Input:
//   A backup directory produced by authagonal-backup, containing:
//     <TableName>.jsonl    (one JSON object per line)
//     _manifest.json       (optional, metadata)
// ---------------------------------------------------------------------------

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

var connectionString = GetArg(cliArgs, "--connection-string") ?? Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
var inputDir = GetArg(cliArgs, "--input");
var tableFilter = GetArg(cliArgs, "--tables")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var dryRun = HasFlag(cliArgs, "--dry-run");
var mode = GetArg(cliArgs, "--mode") ?? "upsert";

if (connectionString is null || inputDir is null || HasFlag(cliArgs, "--help"))
{
    Console.WriteLine("""
    Authagonal Table Storage Restore Tool

    Usage:
      authagonal-restore --connection-string <conn> --input <dir> [options]

    Options:
      --connection-string <conn>   Azure Table Storage connection string
                                   (or set STORAGE_CONNECTION_STRING env var)
      --input <dir>                Backup directory to restore from
      --tables <t1,t2,...>         Comma-separated list of tables to restore (default: all .jsonl files)
      --mode <mode>                Restore mode: upsert (default), merge, or clean
      --dry-run                    Show what would be restored without writing
      --help                       Show this help

    Modes:
      upsert   Insert or replace each entity (default)
      merge    Insert or merge — existing properties not in backup are preserved
      clean    Delete all rows in each table before restoring
    """);
    return connectionString is null || inputDir is null ? 1 : 0;
}

if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"Error: input directory not found: {inputDir}");
    return 1;
}

if (mode is not ("upsert" or "merge" or "clean"))
{
    Console.Error.WriteLine($"Error: invalid mode '{mode}'. Must be upsert, merge, or clean.");
    return 1;
}

// Discover .jsonl files in the input directory
var jsonlFiles = Directory.GetFiles(inputDir, "*.jsonl")
    .Where(f => !Path.GetFileName(f).StartsWith("_"))
    .ToArray();

if (jsonlFiles.Length == 0)
{
    Console.Error.WriteLine($"Error: no .jsonl files found in {inputDir}");
    return 1;
}

// Determine which tables to restore
var availableTables = jsonlFiles.Select(f => Path.GetFileNameWithoutExtension(f)).ToArray();
var tablesToRestore = tableFilter ?? availableTables;

// Validate requested tables exist in backup
foreach (var t in tablesToRestore)
{
    if (!availableTables.Contains(t))
    {
        Console.Error.WriteLine($"Error: table '{t}' not found in backup. Available: {string.Join(", ", availableTables)}");
        return 1;
    }
}

// Read manifest if available
var manifestPath = Path.Combine(inputDir, "_manifest.json");
if (File.Exists(manifestPath))
{
    try
    {
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<JsonElement>(manifestText);
        if (manifest.TryGetProperty("BackupTimestamp", out var ts))
            Console.WriteLine($"Backup timestamp: {ts}");
        if (manifest.TryGetProperty("Mode", out var m))
            Console.WriteLine($"Backup mode: {m}");
    }
    catch
    {
        // Manifest is informational only
    }
}

var clientOptions = new TableClientOptions();
clientOptions.Retry.MaxRetries = 5;
clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(500);
clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(30);
clientOptions.Retry.Mode = RetryMode.Exponential;

var serviceClient = new TableServiceClient(connectionString, clientOptions);

Console.WriteLine($"Input directory: {inputDir}");
Console.WriteLine($"Tables: {string.Join(", ", tablesToRestore)}");
Console.WriteLine($"Mode: {mode}");
if (dryRun) Console.WriteLine("DRY RUN — no data will be written");
Console.WriteLine();

var sw = Stopwatch.StartNew();
long totalEntities = 0;
long totalErrors = 0;

foreach (var tableName in tablesToRestore)
{
    var filePath = Path.Combine(inputDir, $"{tableName}.jsonl");
    if (!File.Exists(filePath))
    {
        Console.WriteLine($"  {tableName}: file not found (skipped)");
        continue;
    }

    var tableClient = serviceClient.GetTableClient(tableName);
    var tableStart = Stopwatch.StartNew();

    if (!dryRun)
    {
        // Ensure table exists
        await tableClient.CreateIfNotExistsAsync();

        // In clean mode, delete all existing entities first
        if (mode == "clean")
        {
            Console.WriteLine($"  {tableName}: cleaning existing data...");
            long deleted = 0;
            await foreach (var existing in tableClient.QueryAsync<TableEntity>(select: new[] { "PartitionKey", "RowKey" }))
            {
                try
                {
                    await tableClient.DeleteEntityAsync(existing.PartitionKey, existing.RowKey, ETag.All);
                    deleted++;
                }
                catch (RequestFailedException)
                {
                    // Entity may have been deleted concurrently
                }
            }
            if (deleted > 0)
                Console.WriteLine($"  {tableName}: deleted {deleted:N0} existing entities");
        }
    }

    long count = 0;
    long errors = 0;

    await foreach (var line in ReadLinesAsync(filePath))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"  {tableName}: skipping malformed line: {ex.Message}");
            errors++;
            continue;
        }

        if (!json.TryGetProperty("PartitionKey", out var pk) || !json.TryGetProperty("RowKey", out var rk))
        {
            Console.Error.WriteLine($"  {tableName}: skipping entity without PartitionKey/RowKey");
            errors++;
            continue;
        }

        var entity = new TableEntity(pk.GetString()!, rk.GetString()!);

        // Add all properties except metadata
        foreach (var prop in json.EnumerateObject())
        {
            if (prop.Name is "PartitionKey" or "RowKey" or "Timestamp" or "ETag" or "odata.etag")
                continue;

            entity[prop.Name] = ConvertJsonValue(prop.Value);
        }

        if (!dryRun)
        {
            try
            {
                if (mode == "merge")
                {
                    await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Merge);
                }
                else
                {
                    await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                }
            }
            catch (RequestFailedException ex)
            {
                Console.Error.WriteLine($"  {tableName}: error restoring {pk}/{rk}: {ex.Message}");
                errors++;
                continue;
            }
        }

        count++;
    }

    tableStart.Stop();
    totalEntities += count;
    totalErrors += errors;

    var errSuffix = errors > 0 ? $", {errors} errors" : "";
    Console.WriteLine($"  {tableName}: {count:N0} entities restored ({tableStart.Elapsed.TotalSeconds:F1}s{errSuffix})");
}

sw.Stop();
Console.WriteLine();
Console.WriteLine($"Done: {totalEntities:N0} entities in {sw.Elapsed.TotalSeconds:F1}s");
if (totalErrors > 0)
    Console.WriteLine($"Warnings: {totalErrors:N0} entities had errors");

return totalErrors > 0 ? 2 : 0;

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

static async IAsyncEnumerable<string> ReadLinesAsync(string path)
{
    using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
    while (await reader.ReadLineAsync() is { } line)
        yield return line;
}

/// <summary>
/// Converts a JSON value back to the appropriate .NET type for Table Storage.
/// Table Storage supports: string, int32, int64, double, bool, DateTime, byte[], Guid.
/// The backup tool serializes all values as JSON, so we need to infer types.
/// </summary>
static object? ConvertJsonValue(JsonElement value)
{
    return value.ValueKind switch
    {
        JsonValueKind.String => TryParseTypedString(value.GetString()!),
        JsonValueKind.Number => value.TryGetInt32(out var i) ? i
            : value.TryGetInt64(out var l) ? l
            : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        // Arrays and objects: serialize back to JSON string for storage
        _ => value.GetRawText(),
    };
}

/// <summary>
/// Attempts to parse string values that may represent DateTimeOffset or Guid.
/// Table Storage stores these as typed values, but JSON round-trips them as strings.
/// </summary>
static object TryParseTypedString(string s)
{
    // Try DateTimeOffset (ISO 8601 format from backup)
    if (s.Length >= 19 && s.Length <= 35 && DateTimeOffset.TryParse(s, out var dto))
        return dto;

    // Try Guid
    if (s.Length == 36 && Guid.TryParse(s, out var guid))
        return guid;

    return s;
}
