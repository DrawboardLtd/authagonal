using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Authagonal.Migration;

public static class ServiceCollectionExtensions
{
    private const string MigrationStateTableName = "MigrationState";

    /// <summary>
    /// Registers the one-time Duende → Authagonal migration runner. Call AFTER <c>AddAuthagonal</c>
    /// (it depends on the stores, <see cref="ISecretProvider"/>, <see cref="RecoveryCodeService"/>,
    /// and the DI-registered <c>ClusterLeaderService</c>). Reads the <c>Migration</c> config section;
    /// the runner is a no-op unless <c>Migration:Enabled</c> is true.
    /// </summary>
    public static IServiceCollection AddAuthagonalDuendeMigration(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DuendeMigrationOptions.SectionName).Get<DuendeMigrationOptions>()
                      ?? new DuendeMigrationOptions();
        services.TryAddSingleton(options);

        // MigrationState table — self-contained wiring off the same Storage:* config the stores use,
        // mirroring AddAuthagonalServerSideSessions.
        var stateTable = BuildMigrationStateTable(configuration);
        services.TryAddSingleton(new MigrationStateStore(stateTable));

        // The engine writes through the DI-registered store singletons.
        services.TryAddSingleton(sp => new DuendeMigrationStores
        {
            Users = sp.GetRequiredService<IUserStore>(),
            Roles = sp.GetRequiredService<IRoleStore>(),
            Scopes = sp.GetRequiredService<IScopeStore>(),
            Clients = sp.GetRequiredService<IClientStore>(),
            Mfa = sp.GetRequiredService<IMfaStore>(),
            SamlProviders = sp.GetRequiredService<ISamlProviderStore>(),
            OidcProviders = sp.GetRequiredService<IOidcProviderStore>(),
            SsoDomains = sp.GetRequiredService<ISsoDomainStore>(),
            Grants = sp.GetRequiredService<IGrantStore>(),
        });

        services.TryAddSingleton(sp => new DuendeMigrationEngine(
            sp.GetRequiredService<DuendeMigrationStores>(),
            sp.GetRequiredService<ISecretProvider>(),
            sp.GetRequiredService<RecoveryCodeService>(),
            sp.GetService<ILogger<DuendeMigrationEngine>>()));

        services.AddHostedService<DuendeMigrationHostedRunner>();

        // The status endpoint has to be mapped by the host — this package references Authagonal.Server, so
        // MapAuthagonalEndpoints cannot reach it. This pair makes the omission report itself at startup
        // instead of surfacing as a 404 on the one request the documented cutover tells an operator to make.
        services.TryAddSingleton<MigrationEndpointRegistration>();
        services.AddHostedService<MigrationStatusEndpointCheck>();

        return services;
    }

    private static TableClient BuildMigrationStateTable(IConfiguration configuration)
    {
        var connectionString = configuration["Storage:ConnectionString"];
        var tableServiceUri = configuration["Storage:TableServiceUri"];

        TableServiceClient serviceClient;
        if (!string.IsNullOrWhiteSpace(tableServiceUri))
            serviceClient = new TableServiceClient(new Uri(tableServiceUri), new DefaultAzureCredential());
        else if (!string.IsNullOrWhiteSpace(connectionString))
            serviceClient = new TableServiceClient(connectionString);
        else
            throw new InvalidOperationException(
                "Duende migration needs storage: set Storage:TableServiceUri (managed identity) or Storage:ConnectionString.");

        var table = serviceClient.GetTableClient(MigrationStateTableName);
        table.CreateIfNotExists();
        return table;
    }
}
