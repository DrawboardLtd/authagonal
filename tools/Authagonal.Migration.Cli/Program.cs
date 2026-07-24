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

var stores = StoreFactory.Create(targetConnectionString);
var engine = new DuendeMigrationEngine(stores, new PlaintextSecretProvider(), new RecoveryCodeService());

var report = await engine.RunAsync(options);

Console.WriteLine();
Console.WriteLine("=== Report ===");
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

if (report.DryRun)
    Console.WriteLine("\n** DRY RUN — no data was written **");

return report.Errors.Count == 0 ? 0 : 2;
