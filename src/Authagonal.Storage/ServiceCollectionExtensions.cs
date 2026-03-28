using Azure.Core;
using Azure.Data.Tables;
using Authagonal.Core.Stores;
using Authagonal.Storage.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Authagonal.Storage;

public static class ServiceCollectionExtensions
{
    private const string UsersTableName = "Users";
    private const string UserEmailsTableName = "UserEmails";
    private const string UserLoginsTableName = "UserLogins";
    private const string ClientsTableName = "Clients";
    private const string GrantsTableName = "Grants";
    private const string GrantsBySubjectTableName = "GrantsBySubject";
    private const string SigningKeysTableName = "SigningKeys";
    private const string SsoDomainsTableName = "SsoDomains";
    private const string SamlProvidersTableName = "SamlProviders";
    private const string OidcProvidersTableName = "OidcProviders";
    private const string GrantsByExpiryTableName = "GrantsByExpiry";
    private const string SamlReplayCacheTableName = "SamlReplayCache";
    private const string OidcStateStoreTableName = "OidcStateStore";
    private const string UserProvisionsTableName = "UserProvisions";

    public static IServiceCollection AddTableStorage(this IServiceCollection services, string connectionString)
    {
        var clientOptions = new TableClientOptions();
        clientOptions.Retry.MaxRetries = 5;
        clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(500);
        clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(30);
        clientOptions.Retry.Mode = RetryMode.Exponential;

        var serviceClient = new TableServiceClient(connectionString, clientOptions);

        // Eagerly create all table clients (and auto-create tables).
        var users = EnsureTable(serviceClient, UsersTableName);
        var userEmails = EnsureTable(serviceClient, UserEmailsTableName);
        var userLogins = EnsureTable(serviceClient, UserLoginsTableName);
        var clients = EnsureTable(serviceClient, ClientsTableName);
        var grants = EnsureTable(serviceClient, GrantsTableName);
        var grantsBySubject = EnsureTable(serviceClient, GrantsBySubjectTableName);
        var grantsByExpiry = EnsureTable(serviceClient, GrantsByExpiryTableName);
        var signingKeys = EnsureTable(serviceClient, SigningKeysTableName);
        var ssoDomains = EnsureTable(serviceClient, SsoDomainsTableName);
        var samlProviders = EnsureTable(serviceClient, SamlProvidersTableName);
        var oidcProviders = EnsureTable(serviceClient, OidcProvidersTableName);
        var samlReplayCache = EnsureTable(serviceClient, SamlReplayCacheTableName);
        var oidcStateStore = EnsureTable(serviceClient, OidcStateStoreTableName);
        var userProvisions = EnsureTable(serviceClient, UserProvisionsTableName);

        // Register store implementations as singletons.
        services.AddSingleton<IUserStore>(new TableUserStore(users, userEmails, userLogins));
        services.AddSingleton<IClientStore>(new TableClientStore(clients));
        services.AddSingleton<IGrantStore>(sp =>
            new TableGrantStore(grants, grantsBySubject, grantsByExpiry, sp.GetRequiredService<ILoggerFactory>().CreateLogger<TableGrantStore>()));
        services.AddSingleton<ISigningKeyStore>(new TableSigningKeyStore(signingKeys));
        services.AddSingleton<ISsoDomainStore>(new TableSsoDomainStore(ssoDomains));
        services.AddSingleton<ISamlProviderStore>(new TableSamlProviderStore(samlProviders));
        services.AddSingleton<IOidcProviderStore>(new TableOidcProviderStore(oidcProviders));
        services.AddSingleton<IUserProvisionStore>(new TableUserProvisionStore(userProvisions));

        // Register grant table clients as keyed singletons for the reconciliation service.
        services.AddKeyedSingleton("Grants", grants);
        services.AddKeyedSingleton("GrantsBySubject", grantsBySubject);
        services.AddKeyedSingleton("GrantsByExpiry", grantsByExpiry);

        // Register the replay cache TableClient as a named singleton so SAML services can consume it.
        services.AddKeyedSingleton("SamlReplayCache", samlReplayCache);

        // Register the OIDC state store TableClient as a named singleton.
        services.AddKeyedSingleton("OidcStateStore", oidcStateStore);

        return services;
    }

    private static TableClient EnsureTable(TableServiceClient serviceClient, string tableName)
    {
        var tableClient = serviceClient.GetTableClient(tableName);
        tableClient.CreateIfNotExists();
        return tableClient;
    }
}
