using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Migration.Cli;

/// <summary>
/// Builds a <see cref="DuendeMigrationStores"/> straight off a Table Storage connection string for the
/// CLI (no DI host). Mirrors the table wiring in
/// <c>Authagonal.AzureProvider/ServiceCollectionExtensions.cs</c>; extended over the old tool with the
/// role, scope and MFA stores the engine now writes.
/// </summary>
internal static class StoreFactory
{
    public static DuendeMigrationStores Create(string connectionString)
    {
        var serviceClient = new TableServiceClient(connectionString);
        var partitioner = EnvPartitioner.Live;

        TableClient Table(string name)
        {
            var client = serviceClient.GetTableClient(name);
            client.CreateIfNotExists();
            return client;
        }

        return new DuendeMigrationStores
        {
            Users = new TableUserStore(
                Table("Users"), Table("UserEmails"), Table("UserLogins"), Table("UserExternalIds"),
                Table("UserFirstNames"), Table("UserLastNames"), partitioner),
            Roles = new TableRoleStore(Table("Roles"), partitioner),
            Scopes = new TableScopeStore(Table("Scopes"), partitioner),
            Clients = new TableClientStore(Table("Clients"), partitioner),
            Mfa = new TableMfaStore(Table("MfaCredentials"), Table("MfaChallenges"), Table("MfaWebAuthnIndex"), partitioner),
            SamlProviders = new TableSamlProviderStore(Table("SamlProviders"), partitioner),
            OidcProviders = new TableOidcProviderStore(Table("OidcProviders"), partitioner),
            SsoDomains = new TableSsoDomainStore(Table("SsoDomains"), partitioner),
            Grants = new TableGrantStore(
                Table("Grants"), Table("GrantsBySubject"), Table("GrantsByExpiry"),
                partitioner, NullLogger<TableGrantStore>.Instance),
        };
    }
}
