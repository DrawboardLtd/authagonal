using Authagonal.Core.Stores;
using Authagonal.Storage.Stores;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Migration;

internal sealed class StoreFactory
{
    public required IUserStore UserStore { get; init; }
    public required IClientStore ClientStore { get; init; }
    public required IGrantStore GrantStore { get; init; }
    public required ISsoDomainStore SsoDomainStore { get; init; }
    public required ISamlProviderStore SamlProviderStore { get; init; }
    public required IOidcProviderStore OidcProviderStore { get; init; }

    public static StoreFactory Create(string connectionString)
    {
        var serviceClient = new TableServiceClient(connectionString);

        var users = EnsureTable(serviceClient, "Users");
        var userEmails = EnsureTable(serviceClient, "UserEmails");
        var userFirstNames = EnsureTable(serviceClient, "UserFirstNames");
        var userLastNames = EnsureTable(serviceClient, "UserLastNames");
        var userLogins = EnsureTable(serviceClient, "UserLogins");
        var clients = EnsureTable(serviceClient, "Clients");
        var grants = EnsureTable(serviceClient, "Grants");
        var grantsBySubject = EnsureTable(serviceClient, "GrantsBySubject");
        var grantsByExpiry = EnsureTable(serviceClient, "GrantsByExpiry");
        var ssoDomains = EnsureTable(serviceClient, "SsoDomains");
        var samlProviders = EnsureTable(serviceClient, "SamlProviders");
        var oidcProviders = EnsureTable(serviceClient, "OidcProviders");
        var userExternalIds = EnsureTable(serviceClient, "UserExternalIds");

        return new StoreFactory
        {
            UserStore = new TableUserStore(users, userEmails, userLogins, userExternalIds, userFirstNames, userLastNames),
            ClientStore = new TableClientStore(clients),
            GrantStore = new TableGrantStore(grants, grantsBySubject, grantsByExpiry, NullLogger<TableGrantStore>.Instance),
            SsoDomainStore = new TableSsoDomainStore(ssoDomains),
            SamlProviderStore = new TableSamlProviderStore(samlProviders),
            OidcProviderStore = new TableOidcProviderStore(oidcProviders)
        };
    }

    private static TableClient EnsureTable(TableServiceClient serviceClient, string tableName)
    {
        var tableClient = serviceClient.GetTableClient(tableName);
        tableClient.CreateIfNotExists();
        return tableClient;
    }
}
