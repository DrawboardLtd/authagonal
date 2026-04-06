using Azure.Core;
using Azure.Data.Tables;
using Authagonal.Backup;

// ---------------------------------------------------------------------------
// Authagonal Table Storage Restore CLI
// Thin wrapper over Authagonal.Backup library.
// ---------------------------------------------------------------------------

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

var connectionString = GetArg(cliArgs, "--connection-string") ?? Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
var inputDir = GetArg(cliArgs, "--input");
var tableFilter = GetArg(cliArgs, "--tables")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var prefix = GetArg(cliArgs, "--prefix") ?? "";
var modeStr = GetArg(cliArgs, "--mode") ?? "upsert";
var dryRun = HasFlag(cliArgs, "--dry-run");

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
      --tables <t1,t2,...>         Comma-separated list of tables to restore
      --prefix <prefix>            Table name prefix (for multi-tenant)
      --mode <mode>                Restore mode: upsert (default), merge, or clean
      --dry-run                    Show what would be restored without writing
      --help                       Show this help
    """);
    return (connectionString is null || inputDir is null) && !HasFlag(cliArgs, "--help") ? 1 : 0;
}

var mode = modeStr.ToLowerInvariant() switch
{
    "merge" => RestoreMode.Merge,
    "clean" => RestoreMode.Clean,
    _ => RestoreMode.Upsert,
};

var clientOptions = new TableClientOptions();
clientOptions.Retry.MaxRetries = 5;
clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(500);
clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(30);
clientOptions.Retry.Mode = RetryMode.Exponential;

var serviceClient = new TableServiceClient(connectionString, clientOptions);

// Determine the backup ID from the input path
var rootDir = Path.GetDirectoryName(inputDir)!;
var backupId = Path.GetFileName(inputDir);
var source = new FileSystemBackupSource(rootDir);

var options = new RestoreOptions
{
    Tables = tableFilter,
    TablePrefix = string.IsNullOrEmpty(prefix) ? null : prefix,
    Mode = mode,
    DryRun = dryRun,
};

var service = new RestoreService(serviceClient, source, options);
var result = await service.RunAsync(backupId);

Console.WriteLine();
foreach (var (table, info) in result.Tables)
{
    var errorSuffix = info.Errors > 0 ? $" ({info.Errors} errors)" : "";
    Console.WriteLine($"  {table}: {info.Restored:N0} entities restored{errorSuffix}");
}
Console.WriteLine($"Done: {result.TotalRestored:N0} entities restored, {result.TotalErrors} errors");

return result.TotalErrors > 0 ? 2 : 0;

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static bool HasFlag(string[] args, string name) => args.Contains(name);
