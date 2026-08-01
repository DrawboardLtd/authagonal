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
var cleanEnv = GetArg(cliArgs, "--clean-env");
var allowCleanIncremental = HasFlag(cliArgs, "--allow-clean-from-incremental");
var allowCleanAllEnvs = HasFlag(cliArgs, "--allow-clean-all-envs");
var allowUnverified = HasFlag(cliArgs, "--allow-unverified");
var allowUnauthenticatedManifest = HasFlag(cliArgs, "--allow-unauthenticated-manifest");

// RestoreOptions has carried EncryptionKey and ManifestKey since backup encryption and manifest
// signing landed, and this tool set neither: an encrypted archive was unrestorable by the shipped
// CLI, and a signed manifest was never checked by it. Both keys must come from outside the backup
// target — a vault entry, a KMS key, an environment secret on the restoring host — or they protect
// nothing.
byte[]? encryptionKey = ReadKey(cliArgs, "--encryption-key", exactBytes: 32);
byte[]? manifestKey = ReadKey(cliArgs, "--manifest-key", minBytes: 32);
if (KeyError is not null)
{
    Console.Error.WriteLine(KeyError);
    return 1;
}

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
      --clean-env <env>            With --mode clean, wipe only this env's rows
                                   (PartitionKey prefix "<env>|"). Omit only when the
                                   tables hold a single env — otherwise a clean restore
                                   destroys every sibling env in the same table.
      --allow-clean-from-incremental
                                   Permit --mode clean against an incremental backup.
                                   Refused by default: it would leave the table holding
                                   only the delta, destroying every unchanged row.
      --allow-clean-all-envs       Permit --mode clean with no --clean-env, emptying the
                                   whole physical table rather than one env's rows.
      --encryption-key <base64>    32-byte key-encryption key the backup was written with.
                                   Required to restore an encrypted archive.
      --manifest-key <base64>      >=32-byte HMAC key the backup was signed with. Proves the
                                   file hashes in the manifest were not rewritten along with
                                   the files. REQUIRED unless --allow-unauthenticated-manifest.
      --allow-unauthenticated-manifest
                                   Restore without --manifest-key, accepting hashes that
                                   detect corruption but not tampering.
      --allow-unverified           Restore a backup whose manifest carries no file hashes
                                   at all (written before integrity hashing existed).
      --dry-run                    Show what would be restored without writing
                                   (writes nothing and deletes nothing)
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
    CleanEnvPrefix = string.IsNullOrEmpty(cleanEnv) ? null : $"{cleanEnv}|",
    AllowCleanFromIncremental = allowCleanIncremental,
    AllowCleanAllEnvs = allowCleanAllEnvs,
    AllowUnverified = allowUnverified,
    AllowUnauthenticatedManifest = allowUnauthenticatedManifest,
    EncryptionKey = encryptionKey,
    ManifestKey = manifestKey,
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

// Set by ReadKey rather than thrown: top-level statements run before the help block, and a malformed
// key must not stop `--help` from printing.
static byte[]? ReadKey(string[] args, string name, int? exactBytes = null, int? minBytes = null)
{
    var raw = GetArg(args, name);
    if (string.IsNullOrWhiteSpace(raw)) return null;

    byte[] key;
    try { key = Convert.FromBase64String(raw); }
    catch (FormatException)
    {
        KeyError = $"ERROR: {name} must be base64.";
        return null;
    }

    if (exactBytes is { } exact && key.Length != exact)
        KeyError = $"ERROR: {name} must decode to exactly {exact} bytes (got {key.Length}).";
    else if (minBytes is { } min && key.Length < min)
        KeyError = $"ERROR: {name} must decode to at least {min} bytes (got {key.Length}).";

    return KeyError is null ? key : null;
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static bool HasFlag(string[] args, string name) => args.Contains(name);


partial class Program { private static string? KeyError; }
