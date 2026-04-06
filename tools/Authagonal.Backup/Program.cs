using Azure.Core;
using Azure.Data.Tables;
using Authagonal.Backup;

// ---------------------------------------------------------------------------
// Authagonal Table Storage Backup CLI
// Thin wrapper over Authagonal.Backup library.
// ---------------------------------------------------------------------------

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

var connectionString = GetArg(cliArgs, "--connection-string") ?? Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
var outputRoot = GetArg(cliArgs, "--output") ?? "./backups";
var incremental = HasFlag(cliArgs, "--incremental");
var tableFilter = GetArg(cliArgs, "--tables")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var prefix = GetArg(cliArgs, "--prefix") ?? "";
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
      --prefix <prefix>            Table name prefix (for multi-tenant)
      --gzip                       Compress backup files with gzip (.jsonl.gz)
      --dry-run                    Show what would be backed up without writing
      --help                       Show this help
    """);
    return connectionString is null && !HasFlag(cliArgs, "--help") ? 1 : 0;
}

var clientOptions = new TableClientOptions();
clientOptions.Retry.MaxRetries = 5;
clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(500);
clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(30);
clientOptions.Retry.Mode = RetryMode.Exponential;

var serviceClient = new TableServiceClient(connectionString, clientOptions);
var target = new FileSystemBackupTarget(outputRoot);

var options = new BackupOptions
{
    Tables = tableFilter,
    TablePrefix = string.IsNullOrEmpty(prefix) ? null : prefix,
    Incremental = incremental,
    Gzip = useGzip,
    DryRun = dryRun,
};

var service = new BackupService(serviceClient, target, options);
var manifest = await service.RunAsync();

Console.WriteLine();
Console.WriteLine($"Backup: {manifest.BackupId}");
Console.WriteLine($"Mode: {manifest.Mode}");
foreach (var (table, info) in manifest.Tables)
{
    Console.WriteLine($"  {table}: {info.EntityCount:N0} entities ({info.DurationSeconds:F1}s)");
}
if (manifest.TombstoneCount > 0)
    Console.WriteLine($"  Tombstones: {manifest.TombstoneCount:N0}");
Console.WriteLine($"Done: {manifest.TotalEntities:N0} entities in {manifest.DurationSeconds:F1}s");

return 0;

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static bool HasFlag(string[] args, string name) => args.Contains(name);
