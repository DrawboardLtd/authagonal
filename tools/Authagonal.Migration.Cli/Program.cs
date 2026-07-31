using System.Text.Json;
using Authagonal.Migration;
using Authagonal.Migration.Cli;
using Authagonal.Server.Services;
using Microsoft.Extensions.Configuration;

// ---------------------------------------------------------------------------
// Thin CLI wrapper: parse args → build stores → run the shared engine → print the report.
// The engine (Authagonal.Migration package) owns all the migration logic; this is only a host.
//
//   dotnet run -- --Source:ConnectionString "Server=...;Database=Identity;..." \
//                 --Target:ConnectionString "UseDevelopmentStorage=true" \
//                 --DryRun true --UsersMode CreateOnly
// ---------------------------------------------------------------------------
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddCommandLine(args)
    .Build();

var options = config.Get<DuendeMigrationOptions>() ?? new DuendeMigrationOptions();

// The engine reads the SQL source from Options.Source.ConnectionString; the Table Storage TARGET is a
// CLI-only concept (the hosted runner writes through DI-registered stores instead).
if (string.IsNullOrWhiteSpace(options.Source.ConnectionString))
{
    Console.Error.WriteLine("ERROR: --Source:ConnectionString is required (the Duende SQL Server).");
    return 1;
}

var targetConnectionString = config["Target:ConnectionString"];
if (string.IsNullOrWhiteSpace(targetConnectionString))
{
    Console.Error.WriteLine("ERROR: --Target:ConnectionString is required (Azure Table Storage).");
    return 1;
}

Console.WriteLine("Duende → Authagonal migration (CLI)");
Console.WriteLine($"  Source:  SQL Server");
Console.WriteLine($"  Target:  Azure Table Storage");
Console.WriteLine($"  DryRun:  {options.DryRun}");
Console.WriteLine($"  Users:   {options.UsersMode}");
Console.WriteLine($"  Clients: {options.MigrateClients}, RefreshTokens: {options.MigrateRefreshTokens}");
Console.WriteLine();

// The secret provider must match the TARGET deployment's, not be assumed.
//
// This hard-coded PlaintextSecretProvider, so every MFA TOTP seed the migration imported was written
// as raw base64 even into a deployment configured with Key Vault — where every seed created through
// the product is a `kv:` reference. Nothing surfaced the difference: KeyVaultSecretProvider returns
// an unprefixed value unchanged as "legacy plaintext", so MFA kept working and the seeds sat in the
// clear permanently. A TOTP seed is a bearer credential; whoever reads one can generate that user's
// second factor forever.
Authagonal.Core.Services.ISecretProvider secretProvider;
// Read from the same section the server binds, so a target that requires vault references gets a
// migration that honours it rather than one quietly writing values the server will later refuse.
var secretProviderOptions = new Authagonal.Core.Services.SecretProviderOptions();
config.GetSection("SecretProvider").Bind(secretProviderOptions);
var vaultUri = config["SecretProvider:VaultUri"];
if (!string.IsNullOrWhiteSpace(vaultUri))
{
    secretProvider = new KeyVaultSecretProvider(
        new Azure.Security.KeyVault.Secrets.SecretClient(
            new Uri(vaultUri), new Azure.Identity.DefaultAzureCredential()),
        secretProviderOptions,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<KeyVaultSecretProvider>.Instance);
    Console.WriteLine($"  Secrets: Key Vault ({vaultUri})");
}
else if (config.GetValue("AllowPlaintextSecrets", false))
{
    secretProvider = new PlaintextSecretProvider();
    Console.WriteLine("  Secrets: PLAINTEXT (--AllowPlaintextSecrets)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("!! MFA TOTP seeds and upstream OIDC client secrets will be written UNPROTECTED.");
    Console.Error.WriteLine("!! A TOTP seed is a bearer credential: whoever reads one generates that user's");
    Console.Error.WriteLine("!! second factor indefinitely. Do not use this against a deployment configured");
    Console.Error.WriteLine("!! with Key Vault — the server cannot tell the two apart and will never say so.");
}
else
{
    Console.Error.WriteLine(
        "ERROR: no secret provider configured. Pass --SecretProvider:VaultUri <https://...> to match a\n" +
        "       Key Vault deployment, or --AllowPlaintextSecrets true to accept that MFA TOTP seeds and\n" +
        "       upstream OIDC client secrets will be written unprotected.");
    return 1;
}

// At-rest PII encryption and blind-index tokenization must match the TARGET deployment's too, and
// unlike the change log neither is configuration-derived: IFieldCipher and IIndexTokenizer are
// registered programmatically by the host (Authagonal Cloud registers a per-tenant Vault Transit
// cipher), so this tool cannot construct them from a connection string. It therefore refuses rather
// than assuming their absence, on the same grounds as the secret provider above.
//
// The divergence this closes is not theoretical: the server's Azure registration DOES resolve both
// seams, so against such a deployment the CLI wrote every user's PII in plaintext into tables whose
// other rows are encrypted — and, worse, wrote plaintext-keyed index rows that a tokenizing reader
// cannot find at all, so the migrated accounts silently could not be looked up by email.
if (!config.GetValue("AllowPlaintextPii", false))
{
    Console.Error.WriteLine(
        "ERROR: this tool writes user PII with no at-rest field encryption and no blind-index\n" +
        "       tokenization, because IFieldCipher / IIndexTokenizer are registered in code, not in\n" +
        "       configuration. If the TARGET deployment registers either, use the in-host runner\n" +
        "       (AddAuthagonalDuendeMigration) — migrating with this tool would write plaintext rows\n" +
        "       and plaintext-keyed indexes the server cannot resolve. If the target registers\n" +
        "       neither, pass --AllowPlaintextPii true to confirm that.");
    return 1;
}

Console.WriteLine();
Console.WriteLine("  PII:     PLAINTEXT (--AllowPlaintextPii) — no field encryption, no blind-index");
Console.WriteLine("           tokenization. Writes ARE captured in the change log, so an incremental");
Console.WriteLine("           backup sees the migrated rows.");
Console.WriteLine();

var stores = StoreFactory.Create(targetConnectionString);
var engine = new DuendeMigrationEngine(stores, secretProvider, new RecoveryCodeService());

var report = await engine.RunAsync(options);

Console.WriteLine();
Console.WriteLine("=== Report ===");
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

if (report.DryRun)
    Console.WriteLine("\n** DRY RUN — no data was written **");

return report.Errors.Count == 0 ? 0 : 2;
