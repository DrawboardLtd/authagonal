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
var vaultUri = config["SecretProvider:VaultUri"];
if (!string.IsNullOrWhiteSpace(vaultUri))
{
    secretProvider = new KeyVaultSecretProvider(
        new Azure.Security.KeyVault.Secrets.SecretClient(
            new Uri(vaultUri), new Azure.Identity.DefaultAzureCredential()),
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

// The CLI writes through stores built without an IFieldCipher, an IIndexTokenizer or an IChangeWriter,
// because none of the three is configuration-derived — a host registers them programmatically. So this
// is stated rather than silently assumed: at-rest PII encryption and blind-index tokenization are NOT
// applied to anything this tool writes, and the writes are not change-log captured.
Console.WriteLine();
Console.WriteLine("  NOTE: this tool writes user PII without at-rest field encryption or blind-index");
Console.WriteLine("        tokenization, and its writes are not captured in the change log (a");
Console.WriteLine("        change-log-driven incremental backup will miss them until the next full");
Console.WriteLine("        scan). A deployment that registers IFieldCipher or IIndexTokenizer should");
Console.WriteLine("        use the in-host runner (AddAuthagonalDuendeMigration) instead.");
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
