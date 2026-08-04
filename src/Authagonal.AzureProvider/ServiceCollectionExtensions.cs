using Azure.Core;
using Azure.Data.Tables;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Authagonal.AzureProvider;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The change log. Physically named "Tombstones" for compatibility — it started as a
    /// delete-only log and the backup engine still reads it under that name.
    /// </summary>
    private const string TombstonesTableName = "Tombstones";

    private const string UsersTableName = "Users";
    private const string UserEmailsTableName = "UserEmails";
    private const string UserFirstNamesTableName = "UserFirstNames";
    private const string UserLastNamesTableName = "UserLastNames";
    private const string UserLoginsTableName = "UserLogins";
    private const string ClientsTableName = "Clients";
    private const string GrantsTableName = "Grants";
    private const string UpstreamRefreshTokensTableName = "UpstreamRefreshTokens";
    private const string GrantsBySubjectTableName = "GrantsBySubject";
    private const string SigningKeysTableName = "SigningKeys";
    private const string SsoDomainsTableName = "SsoDomains";
    private const string SamlProvidersTableName = "SamlProviders";
    private const string OidcProvidersTableName = "OidcProviders";
    private const string GrantsByExpiryTableName = "GrantsByExpiry";
    private const string SamlReplayCacheTableName = "SamlReplayCache";
    private const string OidcStateStoreTableName = "OidcStateStore";
    private const string UserProvisionsTableName = "UserProvisions";
    private const string MfaCredentialsTableName = "MfaCredentials";
    private const string MfaChallengesTableName = "MfaChallenges";
    private const string MfaWebAuthnIndexTableName = "MfaWebAuthnIndex";
    private const string UserExternalIdsTableName = "UserExternalIds";
    private const string UserRolesTableName = "UserRoles";
    private const string ScimTokensTableName = "ScimTokens";
    private const string ScimGroupsTableName = "ScimGroups";
    private const string RateLimitCountersTableName = "RateLimitCounters";
    private const string ScimGroupExternalIdsTableName = "ScimGroupExternalIds";
    private const string ScimGroupRoleMappingsTableName = "ScimGroupRoleMappings";
    private const string RolesTableName = "Roles";
    private const string ScopesTableName = "Scopes";
    private const string RevokedTokensTableName = "RevokedTokens";
    private const string ProvisioningAppsTableName = "ProvisioningApps";
    private const string AgentProfilesTableName = "AgentProfiles";

    public static IServiceCollection AddTableStorage(this IServiceCollection services, string connectionString, bool nameIndexesEnabled = true)
    {
        var serviceClient = new TableServiceClient(connectionString, BuildClientOptions());
        return AddTableStorage(services, serviceClient, nameIndexesEnabled);
    }

    /// <summary>
    /// Managed-identity-friendly overload. Hosts running in AKS / Azure App Service
    /// pass the table service URI (<c>https://{account}.table.core.windows.net/</c>)
    /// and a <see cref="TokenCredential"/> (typically <c>DefaultAzureCredential</c>)
    /// so the storage account never needs an access key in any K8s secret.
    /// </summary>
    /// <param name="nameIndexesEnabled">
    /// When false, <c>UserFirstNames</c> / <c>UserLastNames</c> tables are not
    /// created and writes are skipped. <see cref="IUserStore.SearchAsync"/>
    /// degrades from "email + name prefix" to "email prefix only". Disable when
    /// admin name-prefix search isn't a product feature — these indexes
    /// currently use a single hot partition (<c>PartitionKey = "all"</c>) which
    /// caps writes at ~2k ops/sec and limits scale.
    /// </param>
    public static IServiceCollection AddTableStorage(this IServiceCollection services, Uri tableServiceUri, TokenCredential credential, bool nameIndexesEnabled = true)
    {
        var serviceClient = new TableServiceClient(tableServiceUri, credential, BuildClientOptions());
        return AddTableStorage(services, serviceClient, nameIndexesEnabled);
    }

    private static TableClientOptions BuildClientOptions()
    {
        var clientOptions = new TableClientOptions();
        clientOptions.Retry.MaxRetries = 5;
        clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(500);
        clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(30);
        clientOptions.Retry.Mode = RetryMode.Exponential;
        return clientOptions;
    }

    private static IServiceCollection AddTableStorage(IServiceCollection services, TableServiceClient serviceClient, bool nameIndexesEnabled = true)
    {
        // Eagerly create all table clients (and auto-create tables).
        var users = EnsureTable(serviceClient, UsersTableName);
        var userEmails = EnsureTable(serviceClient, UserEmailsTableName);
        var userFirstNames = nameIndexesEnabled ? EnsureTable(serviceClient, UserFirstNamesTableName) : null;
        var userLastNames = nameIndexesEnabled ? EnsureTable(serviceClient, UserLastNamesTableName) : null;
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
        var mfaCredentials = EnsureTable(serviceClient, MfaCredentialsTableName);
        var mfaChallenges = EnsureTable(serviceClient, MfaChallengesTableName);
        var mfaWebAuthnIndex = EnsureTable(serviceClient, MfaWebAuthnIndexTableName);
        var userExternalIds = EnsureTable(serviceClient, UserExternalIdsTableName);
        var userRoles = EnsureTable(serviceClient, UserRolesTableName);
        var scimTokens = EnsureTable(serviceClient, ScimTokensTableName);
        var scimGroups = EnsureTable(serviceClient, ScimGroupsTableName);
        var scimGroupExternalIds = EnsureTable(serviceClient, ScimGroupExternalIdsTableName);
        var scimGroupRoleMappings = EnsureTable(serviceClient, ScimGroupRoleMappingsTableName);
        var roles = EnsureTable(serviceClient, RolesTableName);
        var scopes = EnsureTable(serviceClient, ScopesTableName);
        var revokedTokens = EnsureTable(serviceClient, RevokedTokensTableName);
        var provisioningApps = EnsureTable(serviceClient, ProvisioningAppsTableName);
        var agentProfiles = EnsureTable(serviceClient, AgentProfilesTableName);
        var upstreamRefreshTokens = EnsureTable(serviceClient, UpstreamRefreshTokensTableName);
        var tombstones = EnsureTable(serviceClient, TombstonesTableName);
        var rateLimitCounters = EnsureTable(serviceClient, RateLimitCountersTableName);

        // Register store implementations as singletons.
        // TryAdd allows multi-tenant hosts to register scoped stores first.
        // Single-tenant hosts only ever serve the live env, so use the live partitioner.
        var live = EnvPartitioner.Live;
        // userRoles is always created: unlike the name indexes it is small, bounded by how many
        // people hold a role, and it is what makes "who administers this" answerable at all.
        // The encryption and blind-index seams are resolved from the container, matching the SQL
        // provider. They were simply not passed: TableUserStore accepts IFieldCipher and
        // IIndexTokenizer, and this registration supplied neither — so a host that had deliberately
        // registered an IFieldCipher (Authagonal Cloud does, per tenant) had every user's PII written
        // in PLAINTEXT on this backend, silently, while believing encryption was in force. The
        // constructor defaults them to null-object passthroughs, which is exactly what made the
        // omission invisible.
        //
        // The change-log (tombstone) seam is wired too, matching the AWS and SQL providers.
        // TableChangeWriter existed but nothing registered it, so on this backend deletes wrote no
        // tombstone at all — and a delete is the one mutation class with no self-heal, because a
        // backup that scans LIVE rows cannot see a row that is gone. An incremental restore therefore
        // RESURRECTED every record deleted since the last full backup: revoked grants, removed role
        // assignments, deprovisioned users.
        var changeWriter = new TableChangeWriter(tombstones);
        services.TryAddSingleton<IChangeWriter>(changeWriter);

        services.TryAddSingleton<IUserStore>(sp => new TableUserStore(
            users, userEmails, userLogins, userExternalIds, userFirstNames, userLastNames, live,
            tombstoneWriter: changeWriter,
            fieldCipher: sp.GetService<IFieldCipher>(),
            indexTokenizer: sp.GetService<IIndexTokenizer>(),
            userRolesTable: userRoles));
        services.TryAddSingleton<IClientStore>(new TableClientStore(clients, live, changeWriter));
        services.TryAddSingleton<IGrantStore>(sp =>
            new TableGrantStore(grants, grantsBySubject, grantsByExpiry, live,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<TableGrantStore>(),
                changeWriter, sp.GetService<IFieldCipher>()));
        // Resolved from the container, like the user and grant stores: a host that registers an
        // IFieldCipher expects the PRIVATE SIGNING KEY to be covered by it above all else.
        services.TryAddSingleton<ISigningKeyStore>(sp =>
            new TableSigningKeyStore(signingKeys, live, changeWriter, sp.GetService<IFieldCipher>()));
        services.TryAddSingleton<ISsoDomainStore>(new TableSsoDomainStore(ssoDomains, live, changeWriter));
        services.TryAddSingleton<ISamlProviderStore>(new TableSamlProviderStore(samlProviders, live, changeWriter));
        services.TryAddSingleton<IOidcProviderStore>(new TableOidcProviderStore(oidcProviders, live, changeWriter));
        services.TryAddSingleton<IUserProvisionStore>(new TableUserProvisionStore(userProvisions, live, changeWriter));
        services.TryAddSingleton<IMfaStore>(new TableMfaStore(mfaCredentials, mfaChallenges, mfaWebAuthnIndex, live, changeWriter));
        services.TryAddSingleton<IScimTokenStore>(new TableScimTokenStore(scimTokens, live, changeWriter));
        services.TryAddSingleton<IScimGroupStore>(new TableScimGroupStore(scimGroups, scimGroupExternalIds, live, changeWriter));
        services.TryAddSingleton<IScimGroupRoleMappingStore>(new TableScimGroupRoleMappingStore(scimGroupRoleMappings, live, changeWriter));
        services.TryAddSingleton<IRoleStore>(new TableRoleStore(roles, live, changeWriter));
        services.TryAddSingleton<IScopeStore>(new TableScopeStore(scopes, live, changeWriter));
        services.TryAddSingleton<IRevokedTokenStore>(new TableRevokedTokenStore(revokedTokens, live));
        // The ApiKey is the outbound Bearer credential the orchestrator sends to a provisioning app —
        // reversible, and encrypted at rest when a cipher is registered. It was not being passed.
        services.TryAddSingleton<IProvisioningAppStore>(sp =>
            new TableProvisioningAppStore(provisioningApps, live, changeWriter, sp.GetService<IFieldCipher>()));
        services.TryAddSingleton<IAgentProfileStore>(new TableAgentProfileStore(agentProfiles, live, changeWriter));
        services.TryAddSingleton<IUpstreamRefreshTokenStore>(sp => new TableUpstreamRefreshTokenStore(upstreamRefreshTokens, live, sp.GetService<IFieldCipher>()));

        // Register grant table clients as keyed singletons for the reconciliation service.
        services.AddKeyedSingleton("Grants", grants);
        services.AddKeyedSingleton("GrantsBySubject", grantsBySubject);
        services.AddKeyedSingleton("GrantsByExpiry", grantsByExpiry);

        // Register the replay cache TableClient as a named singleton so SAML services can consume it.
        services.AddKeyedSingleton("SamlReplayCache", samlReplayCache);

        // Register the OIDC state store TableClient as a named singleton.
        services.AddKeyedSingleton("OidcStateStore", oidcStateStore);

        // Counters for the cluster-wide rate limiter. Registered unconditionally — the table costs
        // nothing until an operator turns durable limiting on — but the limiter itself is opt-in, in
        // AddAuthagonal. The keyed client is what the sweep service takes: Table Storage has no TTL, so
        // unlike DynamoDB and SQL these rows need collecting by something of ours.
        services.TryAddSingleton<IRateLimitCounterStore>(
            new TableRateLimitCounterStore(rateLimitCounters, live));
        services.AddKeyedSingleton("RateLimitCounters", rateLimitCounters);

        // Keyed so the Server can attach TableExpirySweepService to them. Azure Table has no TTL, and
        // nothing collected any of these three: DynamoDB expires them natively and SqlExpiryReaper's table
        // list covers all three, so the DEFAULT backend was the only one accumulating them forever.
        services.AddKeyedSingleton("MfaChallenges", mfaChallenges);
        services.AddKeyedSingleton("RevokedTokens", revokedTokens);
        services.AddKeyedSingleton("UpstreamRefreshTokens", upstreamRefreshTokens);

        return services;
    }

    private static TableClient EnsureTable(TableServiceClient serviceClient, string tableName)
    {
        var tableClient = serviceClient.GetTableClient(tableName);
        tableClient.CreateIfNotExists();
        return tableClient;
    }
}
