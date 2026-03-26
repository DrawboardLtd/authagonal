using Azure.Data.Tables;
using Authagonal.Core.Stores;
using Authagonal.Storage.Stores;
using Microsoft.Extensions.DependencyInjection;

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
    private const string SamlReplayCacheTableName = "SamlReplayCache";
    private const string OidcStateStoreTableName = "OidcStateStore";

    public static IServiceCollection AddTableStorage(this IServiceCollection services, string connectionString)
    {
        var serviceClient = new TableServiceClient(connectionString);

        // Eagerly create all table clients (and auto-create tables).
        var users = EnsureTable(serviceClient, UsersTableName);
        var userEmails = EnsureTable(serviceClient, UserEmailsTableName);
        var userLogins = EnsureTable(serviceClient, UserLoginsTableName);
        var clients = EnsureTable(serviceClient, ClientsTableName);
        var grants = EnsureTable(serviceClient, GrantsTableName);
        var grantsBySubject = EnsureTable(serviceClient, GrantsBySubjectTableName);
        var signingKeys = EnsureTable(serviceClient, SigningKeysTableName);
        var ssoDomains = EnsureTable(serviceClient, SsoDomainsTableName);
        var samlProviders = EnsureTable(serviceClient, SamlProvidersTableName);
        var oidcProviders = EnsureTable(serviceClient, OidcProvidersTableName);
        var samlReplayCache = EnsureTable(serviceClient, SamlReplayCacheTableName);
        var oidcStateStore = EnsureTable(serviceClient, OidcStateStoreTableName);

        // Register store implementations as singletons.
        services.AddSingleton<IUserStore>(new TableUserStore(users, userEmails, userLogins));
        services.AddSingleton<IClientStore>(new TableClientStore(clients));
        services.AddSingleton<IGrantStore>(new TableGrantStore(grants, grantsBySubject));
        services.AddSingleton<ISigningKeyStore>(new TableSigningKeyStore(signingKeys));
        services.AddSingleton<ISsoDomainStore>(new TableSsoDomainStore(ssoDomains));
        services.AddSingleton<ISamlProviderStore>(new TableSamlProviderStore(samlProviders));
        services.AddSingleton<IOidcProviderStore>(new TableOidcProviderStore(oidcProviders));

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
