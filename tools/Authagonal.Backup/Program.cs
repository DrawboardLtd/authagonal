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

byte[]? encryptionKey = null;
var encryptionKeyArg = GetArg(cliArgs, "--encryption-key");
if (!string.IsNullOrWhiteSpace(encryptionKeyArg))
{
    try { encryptionKey = Convert.FromBase64String(encryptionKeyArg); }
    catch (FormatException)
    {
        Console.Error.WriteLine("ERROR: --encryption-key must be base64.");
        return 1;
    }

    if (encryptionKey.Length != 32)
    {
        Console.Error.WriteLine($"ERROR: --encryption-key must decode to exactly 32 bytes (got {encryptionKey.Length}).");
        return 1;
    }
}
// The manifest MAC. BackupOptions has carried this since manifest signing landed and no CLI flag
// ever set it, so every backup the shipped tool produced was unsigned — the library could
// authenticate a manifest that nothing in the release could actually sign.
byte[]? manifestKey = null;
var manifestKeyArg = GetArg(cliArgs, "--manifest-key");
if (!string.IsNullOrWhiteSpace(manifestKeyArg))
{
    try { manifestKey = Convert.FromBase64String(manifestKeyArg); }
    catch (FormatException)
    {
        Console.Error.WriteLine("ERROR: --manifest-key must be base64.");
        return 1;
    }

    if (manifestKey.Length < 32)
    {
        Console.Error.WriteLine($"ERROR: --manifest-key must decode to at least 32 bytes (got {manifestKey.Length}).");
        return 1;
    }
}
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
      --encryption-key <base64>    32-byte AES-256 key-encryption key. Encrypts every data file with
                                   AES-256-GCM under a per-backup content key, wrapped under this one
                                   and recorded in the manifest. Keep it OUTSIDE the backup target —
                                   an envelope whose key sits beside the archive protects nothing.
      --manifest-key <base64>      >=32-byte HMAC key. Signs the manifest, so a restore holding the
                                   same key can prove the recorded file hashes were not rewritten
                                   along with the files they describe. Without it the hashes detect
                                   corruption but not tampering. Keep it OUTSIDE the backup target.
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
    EncryptionKey = encryptionKey,
    ManifestKey = manifestKey,
};

// Say what is about to be written in the clear, before writing it.
//
// The archive is plaintext JSONL and the only warning that existed was about SigningKeys. It is not
// the only table that warrants one: MfaCredentials holds TOTP seeds, which are directly replayable —
// whoever reads one generates that user's second factor indefinitely, undetectably, with no rotation
// short of re-enrolling them. An operator running "back up the identity provider" should not have to
// infer that from the table list.
var selected = (options.Tables ?? BackupDefaults.Tables)
    .Where(t => BackupDefaults.SecretBearingTables.ContainsKey(t))
    .Where(t => options.IncludeSigningKeys || !string.Equals(t, "SigningKeys", StringComparison.OrdinalIgnoreCase))
    .ToList();

if (selected.Count > 0 && !dryRun && encryptionKey is null)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("This backup will contain credential material in CLEARTEXT:");
    foreach (var table in selected)
        Console.Error.WriteLine($"  {table,-24} {BackupDefaults.SecretBearingTables[table]}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Files are created owner-only, which is not encryption. Pass --encryption-key");
    Console.Error.WriteLine("to encrypt the archive, or store it somewhere encrypted and access-controlled");
    Console.Error.WriteLine("and treat it as a credential.");
    Console.Error.WriteLine();
}

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
