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
    /// <param name="fieldCipher">
    /// At-rest field encryption and blind-index tokenization for the TARGET deployment. Null means the
    /// operator declared the target has neither (see Program.cs) — the same tables written by the server
    /// would otherwise be encrypted and tokenized while this tool's rows are not, which is worse than
    /// either state on its own: a plaintext-keyed index row is invisible to a tokenizing reader, so the
    /// migrated user cannot be found by email at all.
    /// </param>
    /// <param name="indexTokenizer"><inheritdoc cref="fieldCipher" path="/summary"/></param>
    public static DuendeMigrationStores Create(
        string connectionString,
        IFieldCipher? fieldCipher = null,
        IIndexTokenizer? indexTokenizer = null)
    {
        var serviceClient = new TableServiceClient(connectionString);
        var partitioner = EnvPartitioner.Live;

        TableClient Table(string name)
        {
            var client = serviceClient.GetTableClient(name);
            client.CreateIfNotExists();
            return client;
        }

        // The change log, wired exactly as the provider wires it. Unlike the cipher and the tokenizer
        // this one IS derivable from the connection string, and it was simply not passed: every row the
        // migration wrote was invisible to a change-log-driven incremental backup, so a restore taken
        // between full scans came back missing the entire migrated population.
        var changeWriter = new TableChangeWriter(Table("Tombstones"));

        return new DuendeMigrationStores
        {
            Users = new TableUserStore(
                Table("Users"), Table("UserEmails"), Table("UserLogins"), Table("UserExternalIds"),
                Table("UserFirstNames"), Table("UserLastNames"), partitioner,
                tombstoneWriter: changeWriter,
                fieldCipher: fieldCipher,
                indexTokenizer: indexTokenizer),
            Roles = new TableRoleStore(Table("Roles"), partitioner, changeWriter),
            Scopes = new TableScopeStore(Table("Scopes"), partitioner, changeWriter),
            Clients = new TableClientStore(Table("Clients"), partitioner, changeWriter),
            Mfa = new TableMfaStore(Table("MfaCredentials"), Table("MfaChallenges"), Table("MfaWebAuthnIndex"), partitioner, changeWriter),
            SamlProviders = new TableSamlProviderStore(Table("SamlProviders"), partitioner, changeWriter),
            OidcProviders = new TableOidcProviderStore(Table("OidcProviders"), partitioner, changeWriter),
            SsoDomains = new TableSsoDomainStore(Table("SsoDomains"), partitioner, changeWriter),
            Grants = new TableGrantStore(
                Table("Grants"), Table("GrantsBySubject"), Table("GrantsByExpiry"),
                partitioner, NullLogger<TableGrantStore>.Instance, changeWriter, fieldCipher),
        };
    }
}
